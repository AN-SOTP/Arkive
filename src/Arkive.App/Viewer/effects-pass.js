// Draws particle effects the way the game composites them: in high dynamic range, on their own layer, over the
// already-drawn scene. Effects add light and translucency into a half-float buffer (so a glow five times brighter
// than white stays five times brighter while effects pile up), test against the scene's depth, and bloom where they
// are brighter than white. The result is brought into range keeping its hue and laid over the scene.
import * as THREE from 'three';

// Particles live on this layer; the scene's own camera does not draw it.
export const EFFECTS_LAYER = 1;

const FULLSCREEN_VERTEX = `
  varying vec2 vUv;
  void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }`;

// The part of the effects brighter than white, which glows.
const BRIGHT = `
  uniform sampler2D source; uniform float threshold;
  varying vec2 vUv;
  void main() {
    vec3 c = texture2D(source, vUv).rgb;
    float m = max(c.r, max(c.g, c.b));
    gl_FragColor = vec4(c * max(0.0, m - threshold) / max(m, 1e-4), 1.0);
  }`;

const BLUR = `
  uniform sampler2D source; uniform vec2 step;
  varying vec2 vUv;
  void main() {
    vec3 sum = texture2D(source, vUv).rgb * 0.227027;
    sum += (texture2D(source, vUv + step * 1.3846).rgb + texture2D(source, vUv - step * 1.3846).rgb) * 0.316216;
    sum += (texture2D(source, vUv + step * 3.2308).rgb + texture2D(source, vUv - step * 3.2308).rgb) * 0.070270;
    gl_FragColor = vec4(sum, 1.0);
  }`;

// Effects are premultiplied (colour already scaled by coverage); the composite keeps the hue of over-bright colour
// and encodes for the screen.
const COMPOSITE = `
  uniform sampler2D effects; uniform sampler2D bloomNear; uniform sampler2D bloomFar; uniform float bloomStrength;
  varying vec2 vUv;
  vec3 encode(vec3 c) { c = clamp(c, 0.0, 1.0); return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
  void main() {
    vec4 fx = texture2D(effects, vUv);
    vec3 glow = (texture2D(bloomNear, vUv).rgb + texture2D(bloomFar, vUv).rgb) * bloomStrength;
    vec3 c = max(fx.rgb, vec3(0.0)) + glow;
    float m = max(c.r, max(c.g, c.b));
    // Brighter than white keeps its hue, washing towards white only as it gets far brighter.
    vec3 ranged = m > 1.0 ? mix(c / m, vec3(1.0), clamp((m - 1.0) / 8.0, 0.0, 0.35)) : c;
    float coverage = clamp(fx.a, 0.0, 1.0);
    gl_FragColor = vec4(encode(ranged), coverage);
  }`;

export function createEffectsPass(renderer) {
  const size = new THREE.Vector2();
  const options = { type: THREE.HalfFloatType, depthBuffer: false, stencilBuffer: false };
  const effects = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, depthBuffer: true, stencilBuffer: false });
  const bloom = [0, 1, 2, 3].map(() => new THREE.WebGLRenderTarget(1, 1, options));
  const depthOnly = new THREE.MeshBasicMaterial({ colorWrite: false });
  const quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2));
  quad.frustumCulled = false;
  const screen = new THREE.Scene();
  screen.add(quad);
  const flat = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
  const shader = (fragmentShader, uniforms, extra = {}) => new THREE.ShaderMaterial({
    vertexShader: FULLSCREEN_VERTEX, fragmentShader, uniforms, depthTest: false, depthWrite: false, ...extra,
  });
  const bright = shader(BRIGHT, { source: { value: null }, threshold: { value: 1 } });
  const blur = shader(BLUR, { source: { value: null }, step: { value: new THREE.Vector2() } });
  const composite = shader(COMPOSITE, {
    effects: { value: effects.texture }, bloomNear: { value: bloom[0].texture }, bloomFar: { value: bloom[3].texture },
    bloomStrength: { value: 0.6 },
  }, {
    transparent: true, blending: THREE.CustomBlending, blendEquation: THREE.AddEquation,
    blendSrc: THREE.OneFactor, blendDst: THREE.OneMinusSrcAlphaFactor,
    blendSrcAlpha: THREE.OneFactor, blendDstAlpha: THREE.OneMinusSrcAlphaFactor,
  });

  function draw(material, target) {
    quad.material = material;
    renderer.setRenderTarget(target);
    renderer.render(screen, flat);
  }

  function resize() {
    renderer.getDrawingBufferSize(size);
    const width = Math.max(1, size.x), height = Math.max(1, size.y);
    if (effects.width !== width || effects.height !== height) effects.setSize(width, height);
    const near = [Math.max(1, width >> 2), Math.max(1, height >> 2)], far = [Math.max(1, width >> 3), Math.max(1, height >> 3)];
    bloom[0].setSize(...near); bloom[1].setSize(...near); bloom[2].setSize(...far); bloom[3].setSize(...far);
  }

  function hasEffects(world) {
    let found = false;
    world.traverseVisible((object) => { if (!found && object.isMesh && object.layers.isEnabled(EFFECTS_LAYER) && object.geometry?.instanceCount !== 0) found = true; });
    return found;
  }

  return {
    // Call after the scene itself has been drawn to the screen (or `target`).
    render(world, camera, target = null) {
      if (!hasEffects(world)) return;
      resize();
      const state = {
        autoClear: renderer.autoClear, background: world.background, override: world.overrideMaterial,
        mask: camera.layers.mask, clearAlpha: renderer.getClearAlpha(), clearColor: renderer.getClearColor(new THREE.Color()),
        target: renderer.getRenderTarget(),
      };
      // The scene's depth, so effects pass behind the character.
      renderer.setRenderTarget(effects);
      renderer.setClearColor(0x000000, 0);
      renderer.clear(true, true, false);
      renderer.autoClear = false;
      world.background = null;
      world.overrideMaterial = depthOnly;
      camera.layers.set(0);
      // Helper lines (the grid, skeleton, normals) do not hide effects.
      const lines = [];
      world.traverseVisible((object) => { if (object.isLine || object.isPoints) lines.push(object); });
      lines.forEach((object) => { object.visible = false; });
      renderer.render(world, camera);
      lines.forEach((object) => { object.visible = true; });
      // The effects, adding up in high dynamic range.
      world.overrideMaterial = null;
      camera.layers.set(EFFECTS_LAYER);
      renderer.render(world, camera);
      camera.layers.mask = state.mask;
      world.background = state.background;
      world.overrideMaterial = state.override;
      // Glow from what is brighter than white, at a quarter and an eighth of the size.
      bright.uniforms.source.value = effects.texture;
      draw(bright, bloom[0]);
      const pass = (from, to, x, y) => {
        blur.uniforms.source.value = from.texture;
        blur.uniforms.step.value.set(x / from.width, y / from.height);
        draw(blur, to);
      };
      pass(bloom[0], bloom[1], 1, 0);
      pass(bloom[1], bloom[0], 0, 1);
      pass(bloom[0], bloom[2], 1, 0);
      pass(bloom[2], bloom[3], 0, 1);
      // Laid over the scene.
      renderer.setClearColor(state.clearColor, state.clearAlpha);
      draw(composite, state.target ?? target);
      renderer.autoClear = state.autoClear;
      renderer.setRenderTarget(state.target);
    },
  };
}
