// Arkive's particle simulation, shared by the particle effect window and the model viewer's skill preview. It plays a
// particle system's emitters from the settings Arkive read out of the game package (the baked value tables the game
// itself samples) with the engine's defaults for settings the package leaves out, and draws them the way the game
// does: camera-facing or velocity-aligned sprites with flipbook frames, flat decals and mesh particles, each shaded by
// its material's own compiled pixel shader from the game's shader cache (translated to GLSL), fed the particle's
// colour, dynamic parameters and texture coordinates as the game's vertex factories feed them. Materials the cache
// has no shader for are approximated by their first texture and blend mode.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { EFFECTS_LAYER, SCENE_DEPTH } from './effects-pass.js';

export const MAX_PARTICLES = 4000;
// Instance buffers start small and double as an emitter needs more, so spawning an effect allocates little.
const FIRST_CAPACITY = 16;

export function defaultRecipe() {
  return { tint: null, tintStrength: 1, brightness: 1, opacity: 1, size: 1, lifetime: 1, spawnRate: 1, speed: 1 };
}

// ---- Edit recipe, the same rules as Arkive's ParticleEffects.Apply ----
export function applyRecipe(table, property, recipe) {
  if (table.modes) return { ...table, base: table.base ? applyRecipe(table.base, property, recipe) : null };
  if (!table.kind) return table;
  const values = table.values.slice();
  const tint = recipe.tint;
  if (table.kind === 'Color' && tint && recipe.tintStrength > 0) {
    const peak = Math.max(tint.red, tint.green, tint.blue);
    const [r, g, b] = [tint.red / peak, tint.green / peak, tint.blue / peak];
    for (let i = 2; i + 2 < values.length; i += 3) {
      const strongest = Math.max(values[i], values[i + 1], values[i + 2]);
      if (strongest <= 0) continue;
      values[i] += (strongest * r - values[i]) * recipe.tintStrength;
      values[i + 1] += (strongest * g - values[i + 1]) * recipe.tintStrength;
      values[i + 2] += (strongest * b - values[i + 2]) * recipe.tintStrength;
    }
  }
  const base = !property.includes('scale');
  const scale = { Color: base ? recipe.brightness : 1, Alpha: base ? recipe.opacity : 1, Size: recipe.size,
    Lifetime: recipe.lifetime, SpawnRate: recipe.spawnRate, Speed: recipe.speed }[table.kind] ?? 1;
  if (scale !== 1) for (let i = 0; i < values.length; i++) values[i] *= scale;
  return { ...table, values };
}

// ---- Baked value tables: two header floats, then samples of ChunkSize floats ----
// A one-dimensional table gives a number; a vector table fills `out` (a new array when none is given).
export function sample(table, time, random, out = null) {
  const dims = table.dimensions, chunk = table.chunkSize, v = table.values;
  const entries = Math.max(1, Math.floor((v.length - 2) / chunk));
  const position = Math.max(0, (time - table.startTime) * table.timeScale);
  const i0 = Math.min(entries - 1, Math.floor(position));
  const i1 = Math.min(entries - 1, i0 + 1);
  const t = Math.min(1, position - i0);
  const ranged = chunk >= dims * 2;
  const o0 = 2 + i0 * chunk, o1 = 2 + i1 * chunk;
  if (dims === 1) {
    let a = v[o0], b = v[o1];
    if (ranged) { const r = random[0]; a += (v[o0 + 1] - a) * r; b += (v[o1 + 1] - b) * r; }
    return a + (b - a) * t;
  }
  const result = out ?? new Array(dims);
  for (let d = 0; d < dims; d++) {
    let a = v[o0 + d], b = v[o1 + d];
    if (ranged) {
      const r = random[d % random.length];
      a += (v[o0 + dims + d] - a) * r;
      b += (v[o1 + dims + d] - b) * r;
    }
    result[d] = a + (b - a) * t;
  }
  return result;
}

// A setting handed in by whoever starts the effect (see ParticleParameterModel): mapped from the value a skill
// passes under its name, or the distribution's constant when none is passed.
function sampleParameter(model, time, random, parameters, out = null) {
  const given = parameters?.[model.name?.toLowerCase()];
  const dims = model.dimensions;
  const result = dims === 1 ? [0] : (out ?? new Array(dims));
  for (let d = 0; d < dims; d++) {
    let value;
    if (given === undefined) value = model.constant[d];
    else {
      const input = Array.isArray(given) ? given[d] ?? given[0] : given;
      const mode = model.modes[d];
      if (mode === 'dpm_direct') value = input;
      else {
        const x = mode === 'dpm_abs' ? Math.abs(input) : input;
        const span = model.maxInput[d] - model.minInput[d];
        const t = span === 0 ? 0 : Math.min(1, Math.max(0, (x - model.minInput[d]) / span));
        value = model.minOutput[d] + (model.maxOutput[d] - model.minOutput[d]) * t;
      }
    }
    result[d] = value;
  }
  if (model.multiply && model.base) {
    if (dims === 1) return sample(model.base, time, random) * result[0];
    const base = sample(model.base, time, random);
    for (let d = 0; d < dims; d++) result[d] *= base[d];
    return result;
  }
  return dims === 1 ? result[0] : result;
}

// Unreal is Z-up; three.js is Y-up.
export const toThree = (v) => new THREE.Vector3(v[0], v[2], v[1]);

// An Unreal rotator (pitch, yaw, roll; degrees, or 65536 units a turn) as a three.js rotation. Unreal is left-handed
// with Z up and the viewer maps (X, Y, Z) to (X, Z, Y): yaw turns about Y the other way, pitch about Z and roll about
// X the other way, applied roll first, then pitch, then yaw.
const AXIS_X = new THREE.Vector3(1, 0, 0), AXIS_Y = new THREE.Vector3(0, 1, 0), AXIS_Z = new THREE.Vector3(0, 0, 1);
export function ueRotator(values, perTurn = 360) {
  const [pitch, yaw, roll] = (values ?? [0, 0, 0]).map((value) => (value ?? 0) * Math.PI * 2 / perTurn);
  return new THREE.Quaternion().setFromAxisAngle(AXIS_Y, -yaw)
    .multiply(new THREE.Quaternion().setFromAxisAngle(AXIS_Z, pitch))
    .multiply(new THREE.Quaternion().setFromAxisAngle(AXIS_X, -roll));
}

// The values a skill passes to an effect it starts scale its colour, opacity, size, lifetime, spawn rate and
// placement; 0 (or a missing value) leaves that part unchanged.
export function effectScales(parameters) {
  const find = (name) => parameters?.find((p) => p.name.toLowerCase() === name);
  const vector = (name) => { const p = find(name); return p && p.isVector && (p.x || p.y || p.z) ? [p.x, p.y, p.z] : null; };
  const scalar = (name) => { const p = find(name); return p && !p.isVector && p.value ? p.value : null; };
  const named = {};
  for (const p of parameters ?? []) named[p.name.toLowerCase()] = p.isVector ? [p.x, p.y, p.z] : p.value;
  return {
    color: vector('color') ?? [1, 1, 1], alpha: scalar('alpha') ?? 1, size: vector('size') ?? (scalar('size') ? Array(3).fill(scalar('size')) : [1, 1, 1]),
    lifetime: scalar('lifetime') ?? 1, spawn: scalar('spawn') ?? 1, location: vector('location') ?? [1, 1, 1], named,
  };
}

// A mesh particle's rotation module gives turns about Unreal's X (roll), Y (pitch) and Z (yaw); as angles about the
// viewer's X, Y and Z axes (see ueRotator for the signs).
const meshTurns = (v) => new THREE.Vector3(-v[0], -v[2], v[1]).multiplyScalar(Math.PI * 2);

const NO_SCALES = effectScales([]);
const WHITE = [1, 1, 1];

// ---- Assets: textures, meshes and shaders by material and mesh name, loaded once and shared ----
export class EffectAssets {
  constructor() {
    this.textures = {};
    this.meshes = {};
    this.prelude = '';
    this.loadedTextures = new Map();
    this.gameTextures = new Map();
    this.loadedMeshes = new Map();
    this.brokenShaders = new Set();
    this.listeners = new Set();
    this.textureLoader = new THREE.TextureLoader().setCrossOrigin('anonymous');
    this.gltfLoader = new GLTFLoader().setCrossOrigin('anonymous');
  }

  add({ textures, meshes, prelude }) {
    Object.assign(this.textures, textures ?? {});
    Object.assign(this.meshes, meshes ?? {});
    if (prelude) this.prelude = prelude;
    this.listeners.forEach((listener) => listener());
  }

  // The approximation's texture: sRGB colour, flipped as three.js flips images.
  texture(material, done) {
    const found = this.textures[material ?? ''];
    if (!found?.url) return null;
    let entry = this.loadedTextures.get(found.url);
    if (!entry) {
      entry = { texture: null, waiting: [] };
      this.loadedTextures.set(found.url, entry);
      this.textureLoader.load(found.url, (texture) => {
        texture.colorSpace = THREE.SRGBColorSpace;
        entry.texture = texture;
        entry.waiting.forEach((callback) => callback(texture));
        entry.waiting = [];
      });
    }
    if (entry.texture) done(entry.texture);
    else entry.waiting.push(done);
    return found;
  }

  // A game shader's texture: raw values (the shader decodes sRGB itself), unflipped, repeating as the game samples.
  gameTexture(url) {
    let texture = this.gameTextures.get(url);
    if (!texture) {
      texture = this.textureLoader.load(url);
      texture.colorSpace = THREE.NoColorSpace;
      texture.flipY = false;
      texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
      this.gameTextures.set(url, texture);
    }
    return texture;
  }

  mesh(name, done) {
    const url = this.meshes[name ?? ''];
    if (!url) return false;
    let entry = this.loadedMeshes.get(url);
    if (!entry) {
      entry = { geometry: null, waiting: [] };
      this.loadedMeshes.set(url, entry);
      this.gltfLoader.load(url, (gltf) => {
        const geometries = [];
        gltf.scene.updateMatrixWorld(true);
        gltf.scene.traverse((node) => {
          if (!node.isMesh) return;
          const geometry = node.geometry.clone();
          geometry.applyMatrix4(node.matrixWorld);
          // UModel's glTF is in metres; particles size meshes in Unreal units (centimetres).
          geometry.scale(100, 100, 100);
          geometries.push(geometry);
        });
        entry.geometry = geometries[0] ?? null;
        entry.waiting.forEach((callback) => callback(entry.geometry));
        entry.waiting = [];
      }, undefined, () => { entry.waiting = []; });
    }
    if (entry.geometry) done(entry.geometry);
    else entry.waiting.push(done);
    return true;
  }

  // Game shaders the browser could not compile (found after a frame is drawn) fall back to the approximation.
  checkPrograms(renderer, effects) {
    let broken = false;
    for (const effect of effects) {
      for (const emitter of effect.emitters) {
        if (!emitter.shaderId || this.brokenShaders.has(emitter.shaderId)) continue;
        const program = renderer.properties.get(emitter.material)?.currentProgram;
        if (program?.diagnostics && program.diagnostics.runnable === false) {
          this.brokenShaders.add(emitter.shaderId);
          console.warn(`Arkive: game shader ${emitter.shaderId} (${emitter.materialName}) did not compile; using the approximation.`,
            program.diagnostics.fragmentShader?.log ?? '');
          broken = true;
        }
      }
    }
    if (broken) this.listeners.forEach((listener) => listener());
  }
}

// ---- Shaders ----
// Every particle passes what the game's vertex factories pass its pixel shader: its colour, dynamic parameters, both
// flipbook frames' texture coordinates, its clip-space position and the camera direction in its tangent space.
const COMMON_VARYINGS = `
  varying vec4 vColor; varying vec4 vDyn; varying vec4 vUv; varying vec4 vClip; varying vec3 vCam; varying vec2 vLocal; varying float vFacing;
  varying float vSub;`;

const SPRITE_VERTEX = `
  attribute vec4 iPos; attribute vec4 iSize; attribute vec3 iVel; attribute vec4 iColor; attribute vec4 iDyn;
  uniform float alignMode; uniform vec2 subImages; uniform float flipUv; uniform vec3 lockU; uniform vec3 lockV; uniform float lift;
  uniform float blendFrames;
  ${COMMON_VARYINGS}
  vec2 frameUv(float frame, vec2 uv, float columns, float rows) {
    frame = mod(floor(frame), columns * rows);
    vec2 cell = vec2(mod(frame, columns), floor(frame / columns));
    vec2 at = (cell + uv) / vec2(columns, rows);
    return flipUv > 0.5 ? vec2(at.x, 1.0 - at.y) : at;
  }
  void main() {
    vec2 corner = position.xy;
    vLocal = corner;
    vFacing = 1.0;
    vec3 center3 = iPos.xyz; float rotation = iPos.w;
    vec4 center;
    float s0 = sin(rotation), c0 = cos(rotation);
    vec2 turned = vec2(c0 * corner.x - s0 * corner.y, s0 * corner.x + c0 * corner.y) * iSize.xy;
    if (alignMode > 2.5) {
      // Turns about a locked axis (lockU) to face the camera as far as it can.
      center = modelViewMatrix * vec4(center3, 1.0);
      float unit = length(modelMatrix[0].xyz);
      vec3 axis = normalize((modelViewMatrix * vec4(lockU, 0.0)).xyz);
      vec3 side = cross(axis, normalize(-center.xyz));
      side = length(side) > 1e-4 ? normalize(side) : vec3(1.0, 0.0, 0.0);
      center.xyz += (side * corner.x * iSize.x + axis * corner.y * iSize.y) * unit;
    } else if (alignMode > 1.5) {
      // Locked to a plane of the effect (lockU, lockV): flat on the ground for decals and Z locks.
      center = modelViewMatrix * vec4(center3 + lockU * turned.x + lockV * turned.y + vec3(0.0, lift, 0.0), 1.0);
    } else {
      center = modelViewMatrix * vec4(center3, 1.0);
      // Sizes are in the effect's own units; view space has the effect's scale (centimetres to metres) applied.
      float unit = length(modelMatrix[0].xyz);
      vec2 offset;
      if (alignMode > 0.5) {
        // Velocity-aligned: the sprite's up axis follows its motion on screen.
        vec2 dir = (modelViewMatrix * vec4(iVel, 0.0)).xy;
        dir = length(dir) > 1e-4 ? normalize(dir) : vec2(0.0, 1.0);
        vec2 side = vec2(dir.y, -dir.x);
        offset = side * corner.x * iSize.x + dir * corner.y * iSize.y;
      } else {
        float s = sin(rotation), c = cos(rotation);
        vec2 scaled = corner * iSize.xy;
        offset = vec2(c * scaled.x - s * scaled.y, s * scaled.x + c * scaled.y);
      }
      center.xy += offset * unit;
    }
    // A camera offset draws the particle nearer the camera (or further) by that many units.
    if (iSize.w != 0.0) center.xyz += normalize(-center.xyz) * iSize.w * length(modelMatrix[0].xyz);
    gl_Position = projectionMatrix * center;
    vClip = gl_Position * 100.0;
    vCam = vec3(0.0, 0.0, 1.0);
    vColor = iColor;
    vDyn = iDyn;
    // Texture coordinates run right and down from the texture's top left, as the game lays out flipbook frames.
    float columns = max(1.0, subImages.x), rows = max(1.0, subImages.y);
    vec2 uv = vec2(corner.x + 0.5, 0.5 - corner.y);
    // Both flipbook frames, the second packed (v, u) as the game's vertex factory packs it, and how far between them.
    float frame = floor(iSize.z);
    vec2 next = frameUv(frame + 1.0, uv, columns, rows);
    vUv = vec4(frameUv(frame, uv, columns, rows), next.y, next.x);
    vSub = blendFrames > 0.5 ? fract(iSize.z) : 0.0;
  }`;

const MESH_VERTEX = `
  attribute vec4 iPos; attribute vec3 iScale; attribute vec4 iQuat; attribute vec4 iColor; attribute vec4 iDyn;
  uniform float flipUv;
  ${COMMON_VARYINGS}
  vec3 rotate(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
  void main() {
    vec3 local = rotate(iQuat, position * iScale);
    vec4 view = modelViewMatrix * vec4(iPos.xyz + local, 1.0);
    gl_Position = projectionMatrix * view;
    vClip = gl_Position * 100.0;
    vec3 n = normalize(normalMatrix * rotate(iQuat, normal / max(iScale, vec3(1e-4))));
    float facing = dot(n, normalize(-view.xyz));
    vFacing = abs(facing);
    vCam = vec3(sqrt(max(0.0, 1.0 - facing * facing)), 0.0, facing);
    vColor = iColor; vDyn = iDyn; vLocal = vec2(0.0); vSub = 0.0;
    vec2 at = flipUv > 0.5 ? vec2(uv.x, 1.0 - uv.y) : uv;
    vUv = vec4(at, at.y, at.x);
  }`;

// The approximation: the material's first texture as a mask, its colour and blend mode.
const FRAGMENT = `
  uniform sampler2D map; uniform float hasMap; uniform float roundEdge; uniform float coverage;
  uniform float texturePower; uniform float strength; uniform float fresnelPower; uniform float fresnelStrength;
  ${COMMON_VARYINGS}
  void main() {
    vec4 tex = hasMap > 0.5 ? texture2D(map, vUv.xy) : vec4(1.0);
    float edge = roundEdge > 0.5 ? smoothstep(0.5, 0.3, length(vLocal)) : 1.0;
    // Exported effect textures are mostly grey masks without alpha: their brightness is the coverage, sharpened by
    // the material's power and scaled by its strength.
    float strongest = max(tex.r, max(tex.g, tex.b));
    float mask = (hasMap > 0.5 ? tex.a * pow(strongest, texturePower) : 1.0) * edge * strength;
    // Fresnel materials show mostly towards a mesh's edges.
    if (fresnelStrength > 0.0) mask *= clamp(pow(1.0 - vFacing, max(0.25, fresnelPower)) * fresnelStrength, 0.0, 1.0);
    float a = min(1.0, vColor.a * mask);
    if (a <= 0.002) discard;
    // The game's colours are HDR (often far above 1); the effects pass brings them into range.
    vec3 hdr = vColor.rgb * (hasMap > 0.5 ? tex.rgb / max(0.001, strongest) : vec3(1.0));
    gl_FragColor = vec4(hdr * a, a * coverage);
  }`;

// What each input of a game particle shader reads, by semantic. The sprite factories pass the particle colour in
// TEXCOORD1 and dynamic parameters in TEXCOORD2; the flipbook, beam, trail and mesh factories pass colour in COLOR0,
// dynamic parameters in COLOR1 and how far between flipbook frames in TEXCOORD1.
function gameInput(input, colorLayout) {
  const register = `ark_in${input.register}`;
  const value = {
    TEXCOORD0: 'vUv', TEXCOORD1: colorLayout ? 'vec4(vSub, 0.0, 0.0, 0.0)' : 'vColor', TEXCOORD2: 'vDyn',
    COLOR0: 'vColor', COLOR1: 'vDyn',
    // Height fog: colour added in xyz, the share that survives in w. The preview has no fog.
    TEXCOORD4: 'vec4(0.0, 0.0, 0.0, 1.0)',
    // Clip-space position (soft particles read the scene depth there; see ParticleShaderPlanner).
    TEXCOORD5: 'vClip',
    TEXCOORD6: 'vec4(vCam, 0.0)',
    // The tangent basis: tangent and normal (its w the binormal sign), facing the camera.
    TEXCOORD10: 'vec4(1.0, 0.0, 0.0, 0.0)', TEXCOORD11: 'vec4(0.0, 0.0, 1.0, 1.0)',
  }[`${input.semantic}${input.index}`];
  if (input.semantic === 'SV_IsFrontFace') return `${register} = gl_FrontFacing ? uvec4(0xFFFFFFFFu) : uvec4(0u);`;
  return `${register} = floatBitsToUint(${value ?? 'vec4(0.0, 0.0, 1.0, 1.0)'});`;
}

// The emitted light of a game shader is linear and HDR; the preview clamps it as the game's LDR output does and
// encodes it for the screen. Translucent shaders write their opacity to o0.w.
// Mesh particles: the game hands the mesh's shader each particle's colour in the engine's constant ahead of the
// material's own (ark_cb0[1]) instead of a vertex input; one draw holds every particle, so it reads the per-particle
// colour instead.
function meshColour(shader) {
  const color0 = shader.inputs.find((input) => input.semantic === 'COLOR' && input.index === 0);
  const usesColor0 = color0 && shader.function.includes(`ark_in${color0.register}`);
  if (usesColor0 || !(shader.firstParameter >= 2)) return shader.function;
  return shader.function.replaceAll('floatBitsToUint(ark_cb0[1])', 'floatBitsToUint(vColor)');
}

function gameFragment(shader, prelude, blend, mesh = false) {
  const uniforms = Object.entries(shader.constantBuffers).map(([name, values]) => `uniform vec4 ${name}[${Math.max(1, values.length / 4)}];`)
    .concat(Object.keys(shader.samplers).map((name) => `uniform sampler2D ${name};`));
  // Linear, unclamped light (the effects pass brings it into range); modulate darkens what is behind by its brightness.
  const output = {
    additive: 'vec3 c = max(o.rgb, vec3(0.0)); if (max(c.r, max(c.g, c.b)) < 0.0005) discard; gl_FragColor = vec4(c, 0.0);',
    modulate: 'float k = dot(clamp(o.rgb, 0.0, 1.0), vec3(0.2126, 0.7152, 0.0722)); gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0 - k);',
    opaque: 'gl_FragColor = vec4(max(o.rgb, vec3(0.0)), 1.0);',
  }[blend] ?? 'float a = clamp(o.a, 0.0, 1.0); if (a < 0.002) discard; gl_FragColor = vec4(max(o.rgb, vec3(0.0)) * a, a);';
  return `${uniforms.join('\n')}
  ${COMMON_VARYINGS}
  ${shader.declarations}
  ${prelude}
  ${mesh ? meshColour(shader) : shader.function}
  void main() {
    ${shader.inputs.map((input) => gameInput(input, shader.inputs.some((item) => item.semantic === 'COLOR'))).join('\n    ')}
    ark_game_shader();
    vec4 o = uintBitsToFloat(ark_o0);
    ${output}
  }`;
}

const QUAD = (() => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([-0.5, -0.5, 0, 0.5, -0.5, 0, 0.5, 0.5, 0, -0.5, 0.5, 0], 3));
  geometry.setIndex([0, 1, 2, 0, 2, 3]);
  return geometry;
})();

// Blend modes by their (lower-case) engine names.
// Effects are drawn into the effects pass's high-dynamic-range buffer premultiplied: translucent colour scaled by its
// coverage, which goes in alpha; additive light with no coverage, so it only adds (see effects-pass.js).
function premultiplied(material) {
  material.blending = THREE.CustomBlending;
  material.blendEquation = THREE.AddEquation;
  material.blendSrc = THREE.OneFactor;
  material.blendDst = THREE.OneMinusSrcAlphaFactor;
  material.blendSrcAlpha = THREE.OneFactor;
  material.blendDstAlpha = THREE.OneMinusSrcAlphaFactor;
  return material;
}

const BLEND = {
  blend_additive: 'additive', blend_translucent: 'translucent', blend_alphacomposite: 'translucent',
  blend_modulate: 'modulate', blend_modulateandadd: 'additive', blend_opaque: 'opaque', blend_masked: 'opaque',
};

// The engine's dynamic parameter value methods.
const DYNAMIC_METHODS = ['edpv_userset', 'edpv_velocityx', 'edpv_velocityy', 'edpv_velocityz', 'edpv_velocitymag'];

// Scratch values for writing particles without allocating.
const scratch = { color: [0, 0, 0], scale: [0, 0, 0], size: [0, 0, 0], m: [0, 0, 0] };
const euler = new THREE.Euler(), quat = new THREE.Quaternion();

// ---- One emitter ----
class Emitter {
  constructor(model, effect) {
    this.model = model;
    this.effect = effect;
    this.visible = model.enabled;
    const required = this.module('required');
    const value = (module, name, fallback) => module?.values?.[name] ?? fallback;
    this.duration = Math.max(0.05, value(required, 'emitterduration', 1));
    this.loops = value(required, 'emitterloops', 0);
    this.delay = value(required, 'emitterdelay', 0);
    this.killOnCompleted = value(required, 'bkilloncompleted', false);
    this.localSpace = value(required, 'buselocalspace', false);
    this.subImages = [value(required, 'subimages_horizontal', 1), value(required, 'subimages_vertical', 1)];
    this.interpolation = value(required, 'interpolationmethod', 'psuvim_none');
    const alignment = value(required, 'screenalignment', 'psa_square');
    this.materialName = model.material;
    this.meshName = model.mesh;
    this.isMesh = !!model.mesh;
    this.isDecal = model.modules.some((module) => module.class.includes('typedatadecal'));
    this.isTrail = model.modules.some((module) => /typedata(beam|trail|ribbon|animtrail)/.test(module.class));
    // Light emitters light the scene and distortion materials bend what is behind them; neither is drawn as particles.
    this.isLight = model.modules.some((module) => module.class.includes('typedatalight'));
    this.isDistortion = /(^|_)(dist|distort|circledisort|worldoffset)(_|\d)|defaultparticle/.test(model.material ?? '') ||
      /distort|(^|[_-])dist([_-]|$)/i.test(model.name ?? '');
    if (this.isLight || this.isDistortion || !model.material || /^(null|none)$/i.test(model.material)) this.visible = false;
    this.alignMode = this.isDecal ? 2 : alignment === 'psa_velocity' ? 1 : 0;
    // Axis locks: a locked plane (epal_x/y/z, its two in-plane axes as lockU and lockV in the viewer's axes) or an axis
    // the sprite turns about (epal_rotate_*).
    this.lockU = [1, 0, 0]; this.lockV = [0, 0, 1]; this.lift = this.isDecal ? 0.5 : 0;
    const lock = this.module('orientationaxislock')?.values?.lockaxisflags ?? 'epal_none';
    const PLANES = {
      epal_x: [[0, 0, 1], [0, 1, 0]], epal_y: [[1, 0, 0], [0, 1, 0]], epal_z: [[1, 0, 0], [0, 0, 1]],
      epal_negative_x: [[0, 0, -1], [0, 1, 0]], epal_negative_y: [[-1, 0, 0], [0, 1, 0]], epal_negative_z: [[1, 0, 0], [0, 0, -1]],
    };
    const AXES = { epal_rotate_x: [1, 0, 0], epal_rotate_y: [0, 0, 1], epal_rotate_z: [0, 1, 0] };
    if (PLANES[lock] && !this.isDecal) { [this.lockU, this.lockV] = PLANES[lock]; this.alignMode = 2; }
    else if (AXES[lock]) { this.lockU = AXES[lock]; this.alignMode = 3; }
    this.cameraOffset = this.module('cameraoffset');
    this.onGround = this.module('locationonground');
    this.squareSprites = alignment === 'psa_square' || alignment === 'psa_typespecific';
    this.sphere = this.module('locationprimitivesphere');
    this.cylinder = this.module('locationprimitivecylinder') ?? this.module('circlesurface');
    // Up to four values per particle the material reads (dissolve, panning...); 1 when the emitter sets none.
    const dynamic = this.module('parameterdynamic');
    this.dynamic = (dynamic?.values?.dynamicparams ?? []).slice(0, 4).map((param) => ({
      table: param.paramvalue && (Array.isArray(param.paramvalue.values) || param.paramvalue.modes) ? param.paramvalue : null,
      method: Math.max(0, DYNAMIC_METHODS.indexOf(param.valuemethod ?? 'edpv_userset')),
      emitterTime: !!param.busemittertime,
      spawnOnly: !!param.bspawntimeonly,
    }));
    this.hasDynamic = !!dynamic;
    this.particles = [];
    this.random = Math.random;
    this.capacity = 0;
    this.buildRenderer();
    this.applyRecipe(effect.recipe);
    this.reset();
  }

  module(fragment) { return this.model.modules.find((module) => module.class.includes(fragment)); }

  applyRecipe(recipe) {
    // Each setting is the first table of that name among the modules; over-life multipliers stack.
    const tables = {};
    const lists = { lifemultiplier: [], colorscaleoverlife: [], alphascaleoverlife: [], startlocation: [] };
    for (const module of this.model.modules) {
      for (const [name, value] of Object.entries(module.values)) {
        if (!value || (!Array.isArray(value.values) && !value.modes)) continue;
        const effective = applyRecipe(value, name, recipe);
        const isSpawnRate = name === 'rate' || name === 'spawnrate';
        if (name in lists) lists[name].push({ table: effective, module });
        else if (!(name in tables) || (isSpawnRate && module.class.includes('spawn'))) tables[name] = effective;
      }
    }
    this.tables = tables;
    this.lists = lists;
    const spawn = this.module('particlemodulespawn') ?? this.module('required');
    this.bursts = (spawn?.values?.burstlist ?? this.module('required')?.values?.burstlist ?? [])
      .map((burst) => ({ count: burst.count ?? 0, low: burst.countlow ?? -1, time: burst.time ?? 0 }));
  }

  value(table, time, random, out = null) {
    return table.modes ? sampleParameter(table, time, random, this.effect.scales.named, out) : sample(table, time, random, out);
  }

  reset() {
    this.particles.length = 0;
    this.spawnIndex = 0;
    this.emitTime = 0;
    this.spawnCarry = 0;
    this.loopIndex = 0;
    this.burstsFired = new Set();
    this.done = false;
  }

  get finishedAt() { return this.loops === 0 ? Infinity : this.delay + this.duration * this.loops; }

  spawnRate(time) {
    const rate = this.tables.rate ?? this.tables.spawnrate;
    // An emitter whose spawn rate the package leaves out emits the engine's default 20 a second.
    let value = rate ? this.value(rate, time, [0.5]) : 20 * this.effect.recipe.spawnRate;
    if (this.tables.ratescale) value *= this.value(this.tables.ratescale, time / this.duration, [0.5]);
    return Math.max(0, value * this.effect.scales.spawn);
  }

  // A particle's dynamic parameters at its relative age (or the emitter's time).
  dynamicValues(p, life) {
    for (let i = 0; i < this.dynamic.length; i++) {
      const param = this.dynamic[i];
      if (param.spawnOnly && p.age > 0) continue;
      let value = param.table ? this.value(param.table, param.emitterTime ? this.emitTime : life, p.seeds) : 1;
      if (Array.isArray(value)) value = value[0];
      if (param.method === 1) value *= p.vel.x;
      else if (param.method === 2) value *= p.vel.z;
      else if (param.method === 3) value *= p.vel.y;
      else if (param.method === 4) value *= p.vel.length();
      p.dyn[i] = value;
    }
  }

  spawn() {
    if (this.particles.length >= MAX_PARTICLES) return;
    const rnd = () => [this.random(), this.random(), this.random()];
    const t = this.tables, s = this.effect.scales, time = this.emitTime;
    // A particle without a lifetime lives until its emitter is done; without a size it is one unit across (mesh
    // particles keep their mesh's size), and the skill that starts the effect usually scales it.
    const life = t.lifetime ? Math.max(0.01, this.value(t.lifetime, time, rnd()) * s.lifetime) : Infinity;
    const size = t.startsize ? this.value(t.startsize, time, rnd()) : [1, 1, 1];
    const sized = [0, 1, 2].map((axis) => Math.abs(size[axis] ?? size[0]) * s.size[axis]);
    // Every location module moves the particle by its own offset.
    const location = [0, 0, 0];
    for (const { table } of this.lists.startlocation) {
      const offset = this.value(table, time, rnd());
      location[0] += offset[0]; location[1] += offset[1]; location[2] += offset[2];
    }
    const p = {
      age: 0, life,
      size: sized,
      pos: toThree(location.map((v, axis) => v * s.location[axis])),
      vel: t.startvelocity ? toThree(this.value(t.startvelocity, time, rnd())) : new THREE.Vector3(),
      color: t.startcolor ? this.value(t.startcolor, time, rnd()) : [1, 1, 1],
      alpha: t.startalpha ? this.value(t.startalpha, time, rnd()) : 1,
      rot: t.startrotation ? this.value(t.startrotation, time, rnd()) * Math.PI * 2 : 0,
      rotRate: t.startrotationrate ? this.value(t.startrotationrate, time, rnd()) * Math.PI * 2 : 0,
      meshRot: t.startrotation && this.isMesh && Array.isArray(this.value(t.startrotation, time, [0.5]))
        ? meshTurns(this.value(t.startrotation, time, rnd())) : new THREE.Vector3(),
      meshRotRate: t.startrotationrate && this.isMesh && Array.isArray(this.value(t.startrotationrate, time, [0.5]))
        ? meshTurns(this.value(t.startrotationrate, time, rnd())) : new THREE.Vector3(),
      frame: this.interpolation.startsWith('psuvim_random') ? Math.floor(this.random() * this.subImages[0] * this.subImages[1]) : 0,
      seeds: rnd(),
      dyn: [1, 1, 1, 1],
      worldScale: 1,
    };
    if (this.isMesh && t.startrotation && !Array.isArray(this.value(t.startrotation, time, [0.5]))) p.meshRot.y = p.rot;
    const sphere = this.sphere, cylinder = this.cylinder;
    if (sphere || cylinder) {
      const radius = t.startradius ? Math.abs(this.value(t.startradius, time, rnd())) : 50;
      const direction = new THREE.Vector3();
      if (sphere) direction.randomDirection();
      else {
        // Lost Ark's circle surface: a full or half circle from its start angle, at random or split evenly.
        const values = cylinder.values;
        const span = values.bhalfmode ? Math.PI : Math.PI * 2;
        const split = values.splitcirclecount ?? 0;
        const share = split > 0 ? ((this.spawnIndex++ % split) + 0.5) / split : this.random();
        const start = values.startrot ? this.value(values.startrot, time, [0.5]) * Math.PI / 180 : 0;
        const a = start + share * span;
        direction.set(Math.cos(a), 0, Math.sin(a));
      }
      const surface = (sphere ?? cylinder).values.surfaceonly ?? cylinder?.class.includes('circlesurface') ?? false;
      const offset = direction.clone().multiplyScalar(radius * (surface ? 1 : Math.cbrt(this.random())));
      if (cylinder && t.startheight) offset.y += (this.random() - 0.5) * this.value(t.startheight, time, rnd());
      p.pos.add(offset);
      if ((sphere ?? cylinder).values.velocity) {
        const vscale = t.velocityscale ? this.value(t.velocityscale, time, rnd()) : 1;
        // The engine's sphere and cylinder push outward by the offset itself; Lost Ark's circle surface by its direction.
        p.vel.add(direction.multiplyScalar(sphere || !cylinder.class.includes('circlesurface') ? radius * vscale : vscale));
      }
    }
    if (t.startvelocityradial) {
      const radial = this.value(t.startvelocityradial, time, rnd());
      if (p.pos.lengthSq() > 0) p.vel.add(p.pos.clone().normalize().multiplyScalar(radial));
    }
    // World-space emitters leave their particles where they were spawned when the effect moves on.
    if (!this.localSpace && this.effect.parent) {
      const parent = this.effect.parent;
      parent.updateWorldMatrix(true, false);
      p.pos.applyMatrix4(parent.matrixWorld);
      const scale = new THREE.Vector3().setFromMatrixScale(parent.matrixWorld).x;
      const speed = p.vel.length();
      p.vel.transformDirection(parent.matrixWorld).multiplyScalar(speed * scale);
      p.size = p.size.map((v) => v * scale);
      p.worldScale = scale;
    }
    // Placed on the ground under where it would be (the character's feet in the preview).
    if (this.onGround) {
      const frame = this.localSpace || !this.effect.parent ? this.effect.group : null;
      if (frame) {
        frame.updateWorldMatrix(true, false);
        const world = p.pos.clone().applyMatrix4(frame.matrixWorld);
        world.y = this.effect.ground;
        p.pos.copy(world.applyMatrix4(frame.matrixWorld.clone().invert()));
      } else p.pos.y = this.effect.ground;
    }
    p.cameraOffset = this.cameraOffset?.values?.cameraoffset ? this.value(this.cameraOffset.values.cameraoffset, time, rnd()) : 0;
    if (this.dynamic.length) this.dynamicValues(p, 0);
    this.particles.push(p);
  }

  update(dt, effectTime, active) {
    const local = effectTime - this.delay;
    if (local >= 0 && !this.done && active) {
      const loopTime = local % this.duration;
      const loop = Math.floor(local / this.duration);
      if (loop !== this.loopIndex) { this.loopIndex = loop; this.burstsFired.clear(); }
      if (this.loops > 0 && loop >= this.loops) {
        this.done = true;
        // Particles without a lifetime last as long as their emitter; the rest live out their lives unless the
        // emitter kills them when it completes.
        if (this.killOnCompleted) this.particles.length = 0;
        else this.keep((p) => Number.isFinite(p.life));
      } else {
        this.emitTime = loopTime;
        this.spawnCarry += this.spawnRate(loopTime) * dt;
        while (this.spawnCarry >= 1) { this.spawn(); this.spawnCarry -= 1; }
        for (let i = 0; i < this.bursts.length; i++) {
          const burst = this.bursts[i];
          if (this.burstsFired.has(i) || loopTime / this.duration < burst.time) continue;
          this.burstsFired.add(i);
          const count = burst.low >= 0 ? burst.low + Math.floor(this.random() * (burst.count - burst.low + 1)) : burst.count;
          for (let n = 0; n < count * Math.max(this.effect.scales.spawn, 0); n++) this.spawn();
        }
      }
    }
    const t = this.tables;
    if (t.acceleration && !this.acceleration) this.acceleration = toThree(this.value(t.acceleration, 0, [0.5, 0.5, 0.5]));
    const acceleration = t.acceleration ? this.acceleration : null;
    this.keep((p) => {
      p.age += dt;
      if (p.age >= p.life) return false;
      if (acceleration) p.vel.addScaledVector(acceleration, dt * p.worldScale);
      p.pos.addScaledVector(p.vel, dt);
      p.rot += p.rotRate * dt;
      if (this.isMesh) p.meshRot.addScaledVector(p.meshRotRate, dt);
      return true;
    });
  }

  // Keeps the particles that pass, in place.
  keep(test) {
    const list = this.particles;
    let kept = 0;
    for (let i = 0; i < list.length; i++) if (test(list[i])) list[kept++] = list[i];
    list.length = kept;
  }

  write() {
    const count = this.visible ? Math.min(this.particles.length, MAX_PARTICLES) : 0;
    if (count > this.capacity) this.allocate(Math.min(MAX_PARTICLES, Math.max(count, this.capacity * 2)));
    const t = this.tables, s = this.effect.scales;
    const frames = this.subImages[0] * this.subImages[1];
    const linearFrames = frames > 1 && (this.interpolation === 'psuvim_linear' || this.interpolation === 'psuvim_linear_blend');
    const tint = this.materialTint ?? WHITE;
    const { positions, colors, dynamics } = this;
    for (let i = 0; i < count; i++) {
      const p = this.particles[i];
      // Settings over life read the particle's relative age; ones that never die stay at their start.
      const life = Number.isFinite(p.life) ? p.age / p.life : 0;
      const color = t.coloroverlife ? this.value(t.coloroverlife, life, p.seeds, scratch.color) : p.color;
      let alpha = t.alphaoverlife ? this.value(t.alphaoverlife, life, p.seeds) : p.alpha;
      let r = color[0] * s.color[0] * tint[0], g = color[1] * s.color[1] * tint[1], b = color[2] * s.color[2] * tint[2];
      alpha *= s.alpha;
      for (const { table } of this.lists.colorscaleoverlife) {
        const scale = this.value(table, life, p.seeds, scratch.scale);
        r *= scale[0]; g *= scale[1]; b *= scale[2];
      }
      for (const { table } of this.lists.alphascaleoverlife) alpha *= this.value(table, life, p.seeds);
      const size = scratch.size;
      size[0] = p.size[0]; size[1] = p.size[1]; size[2] = p.size[2];
      for (const { table, module } of this.lists.lifemultiplier) {
        const m = this.value(table, life, p.seeds, scratch.m);
        const array = Array.isArray(m);
        if (module.values.multiplyx !== false) size[0] *= array ? m[0] : m;
        if (module.values.multiplyy !== false) size[1] *= array ? m[1] : m;
        if (module.values.multiplyz !== false) size[2] *= array ? m[2] : m;
      }
      if (this.dynamic.length) this.dynamicValues(p, life);
      const i3 = i * 3, i4 = i * 4;
      positions[i4] = p.pos.x; positions[i4 + 1] = p.pos.y; positions[i4 + 2] = p.pos.z;
      colors[i4] = Math.max(0, r); colors[i4 + 1] = Math.max(0, g); colors[i4 + 2] = Math.max(0, b);
      colors[i4 + 3] = Math.max(0, alpha);
      dynamics[i4] = p.dyn[0]; dynamics[i4 + 1] = p.dyn[1]; dynamics[i4 + 2] = p.dyn[2]; dynamics[i4 + 3] = p.dyn[3];
      if (this.drawMesh) {
        positions[i4 + 3] = 0;
        // Unreal sizes are X, Y, Z with Z up.
        this.scales[i3] = size[0]; this.scales[i3 + 1] = size[2]; this.scales[i3 + 2] = size[1];
        quat.setFromEuler(euler.set(p.meshRot.x, p.meshRot.y, p.meshRot.z, 'YZX'));
        this.quats[i4] = quat.x; this.quats[i4 + 1] = quat.y; this.quats[i4 + 2] = quat.z; this.quats[i4 + 3] = quat.w;
      } else {
        positions[i4 + 3] = p.rot;
        let frame = p.frame;
        // Fractional frames: the part past a frame blends towards the next where the emitter blends frames.
        if (linearFrames) frame = Math.min(life * frames, frames - 1);
        if (t.subimageindex) frame = this.value(t.subimageindex, life, p.seeds);
        this.sizes[i4] = size[0]; this.sizes[i4 + 1] = this.squareSprites ? size[0] : size[1]; this.sizes[i4 + 2] = frame;
        this.sizes[i4 + 3] = p.cameraOffset;
        this.velocities[i3] = p.vel.x; this.velocities[i3 + 1] = p.vel.y; this.velocities[i3 + 2] = p.vel.z;
      }
    }
    this.geometry.instanceCount = count;
    if (this.timeRates && count > 0) {
      const { base, rates, vectors } = this.timeRates;
      const now = (performance.now() / 1000) % 3600;
      for (let i = 0; i < vectors.length; i++) {
        const at = i * 4;
        vectors[i].set(base[at] + rates[at] * now, (base[at + 1] ?? 0) + (rates[at + 1] ?? 0) * now,
          (base[at + 2] ?? 0) + (rates[at + 2] ?? 0) * now, (base[at + 3] ?? 0) + (rates[at + 3] ?? 0) * now);
      }
    }
    // Only the particles drawn are sent to the graphics card.
    if (count > 0 || this.lastCount > 0) {
      for (const attribute of this.instanced) {
        attribute.clearUpdateRanges();
        attribute.addUpdateRange(0, Math.max(1, count) * attribute.itemSize);
        attribute.needsUpdate = true;
      }
    }
    this.lastCount = count;
  }

  // (Re)creates the instance buffers for `capacity` particles.
  allocate(capacity) {
    const geometry = this.geometry;
    if (this.capacity > 0) geometry.dispose();
    this.capacity = capacity;
    this.instanced = [];
    const instanced = (name, size) => {
      const array = new Float32Array(capacity * size);
      const attribute = new THREE.InstancedBufferAttribute(array, size).setUsage(THREE.DynamicDrawUsage);
      geometry.setAttribute(name, attribute);
      this.instanced.push(attribute);
      return array;
    };
    this.positions = instanced('iPos', 4);
    this.colors = instanced('iColor', 4);
    this.dynamics = instanced('iDyn', 4);
    if (this.drawMesh) {
      this.scales = instanced('iScale', 3);
      this.quats = instanced('iQuat', 4);
    } else {
      this.sizes = instanced('iSize', 4);
      this.velocities = instanced('iVel', 3);
    }
  }

  buildRenderer(meshGeometry = null) {
    if (this.object) {
      this.object.removeFromParent();
      this.geometry.dispose();
      this.material.dispose();
    }
    const geometry = new THREE.InstancedBufferGeometry();
    // A mesh emitter's type data turns its mesh (pitch, yaw, roll in degrees) before particles place it.
    const turn = this.module('typedatamesh')?.values;
    if (meshGeometry && turn && (turn.pitch || turn.yaw || turn.roll)) {
      meshGeometry = meshGeometry.clone().applyQuaternion(ueRotator([turn.pitch ?? 0, turn.yaw ?? 0, turn.roll ?? 0]));
    }
    const base = meshGeometry ?? QUAD;
    geometry.index = base.index;
    geometry.setAttribute('position', base.attributes.position);
    if (base.attributes.uv) geometry.setAttribute('uv', base.attributes.uv);
    if (base.attributes.normal) geometry.setAttribute('normal', base.attributes.normal);
    this.drawMesh = !!meshGeometry;
    this.geometry = geometry;
    this.capacity = 0;
    this.allocate(Math.max(FIRST_CAPACITY, Math.min(MAX_PARTICLES, this.particles.length)));
    geometry.instanceCount = 0;
    this.lastCount = 0;
    this.material = this.createMaterial();
    this.object = new THREE.Mesh(geometry, this.material);
    this.object.frustumCulled = false;
    this.object.layers.set(EFFECTS_LAYER);
    (this.localSpace || !this.effect.parent ? this.effect.group : this.effect.worldGroup).add(this.object);
    this.applyAssets();
  }

  // The material's game shader for this kind of emitter, as the game picks its vertex factory: meshes draw with the
  // local factory, beams and trails with theirs, and sprites with the one for their flipbook and dynamic parameters.
  pickShader() {
    const assets = this.effect.assets;
    const set = assets?.textures[this.materialName ?? '']?.shader;
    if (!set?.factories || this.isLight || this.isDistortion) return null;
    const factories = set.factories;
    let names;
    if (this.drawMesh) names = ['fparticleinstancedmeshvertexfactory', 'flocalvertexfactory'];
    else if (this.isDecal) names = ['flocaldecalvertexfactory', 'flandscapedecalvertexfactory', 'fparticlevertexfactory'];
    else if (this.isMesh) return null;
    else {
      const dynamic = this.hasDynamic ? 'dynamicparameter' : '';
      const subUv = this.interpolation !== 'psuvim_none' ? 'subuv' : '';
      names = this.isTrail ? [`fparticlebeamtrail${dynamic}vertexfactory`, 'fparticlebeamtrailvertexfactory'] : [];
      names.push(`fparticle${subUv}${dynamic}vertexfactory`, `fparticle${subUv}offsetcenter${dynamic}vertexfactory`,
        `fparticle${dynamic}vertexfactory`, `fparticleoffsetcenter${dynamic}vertexfactory`, 'fparticlevertexfactory',
        'fparticledynamicparametervertexfactory', 'fparticlesubuvvertexfactory', 'fparticlepointspritevertexfactory');
      names.push(...Object.keys(factories).filter((name) => name.startsWith('fparticle') && !name.includes('mesh')));
    }
    for (const name of names) {
      const id = factories[name];
      if (id && set.shaders[id] && !assets.brokenShaders.has(id)) return { id, shader: set.shaders[id], set };
    }
    return null;
  }

  createMaterial() {
    this.timeRates = null;
    const picked = this.pickShader();
    this.shaderId = picked?.id ?? null;
    const shared = {
      alignMode: { value: this.alignMode }, subImages: { value: new THREE.Vector2(this.subImages[0], this.subImages[1]) },
      lockU: { value: new THREE.Vector3(...this.lockU) }, lockV: { value: new THREE.Vector3(...this.lockV) }, lift: { value: this.lift },
      blendFrames: { value: /_blend$/.test(this.interpolation) ? 1 : 0 },
    };
    const vertexShader = this.drawMesh ? MESH_VERTEX : SPRITE_VERTEX;
    if (picked) {
      const assets = this.effect.assets;
      const blend = BLEND[(picked.set.blendMode ?? '').toLowerCase()] ?? (assets.textures[this.materialName]?.blend === 'additive' ? 'additive' : 'translucent');
      const uniforms = { ...shared, flipUv: { value: 0 } };
      for (const [name, values] of Object.entries(picked.shader.constantBuffers)) {
        const vectors = [];
        for (let i = 0; i < values.length; i += 4) vectors.push(new THREE.Vector4(values[i], values[i + 1] ?? 0, values[i + 2] ?? 0, values[i + 3] ?? 0));
        uniforms[name] = { value: vectors.length ? vectors : [new THREE.Vector4()] };
        // Time-driven values (panning, scrolling) move on at their own rate as the game's clock runs.
        if (name === 'ark_cb0' && picked.shader.timeRates?.length) this.timeRates = { base: values.slice(), rates: picked.shader.timeRates, vectors };
      }
      let sceneDepth = false;
      for (const [name, url] of Object.entries(picked.shader.samplers)) {
        if (url === 'scene:depth') { uniforms[name] = SCENE_DEPTH; sceneDepth = true; }
        else uniforms[name] = { value: assets.gameTexture(url) };
      }
      const material = new THREE.ShaderMaterial({
        uniforms, vertexShader, fragmentShader: gameFragment(picked.shader, assets.prelude, blend, this.drawMesh),
        transparent: true, depthWrite: false, side: THREE.DoubleSide,
      });
      premultiplied(material);
      material.name = this.materialName ?? '';
      material.userData.sceneDepth = sceneDepth;
      return material;
    }
    return premultiplied(new THREE.ShaderMaterial({
      uniforms: {
        ...shared, flipUv: { value: 1 },
        map: { value: null }, hasMap: { value: 0 }, coverage: { value: 0 },
        roundEdge: { value: this.drawMesh ? 0 : 1 },
        texturePower: { value: 1 }, strength: { value: 1 }, fresnelPower: { value: 1 }, fresnelStrength: { value: 0 },
      },
      vertexShader,
      fragmentShader: FRAGMENT,
      transparent: true, depthWrite: false, side: THREE.DoubleSide,
    }));
  }

  applyAssets() {
    const assets = this.effect.assets;
    if (!assets) return;
    // Requested once: a mesh already loaded calls back at once, and its renderer applies the assets again.
    if (this.isMesh && !this.meshRequested && assets.meshes[this.meshName ?? '']) {
      this.meshRequested = true;
      assets.mesh(this.meshName, (geometry) => { if (geometry && !this.drawMesh) this.buildRenderer(geometry); });
    }
    const found = assets.textures[this.materialName ?? ''];
    if (!found) return;
    // A shader that arrived (or broke) since the material was made replaces it.
    const picked = this.pickShader();
    if ((picked?.id ?? null) !== this.shaderId) {
      this.material.dispose();
      this.material = this.createMaterial();
      this.object.material = this.material;
    }
    this.materialTint = this.shaderId ? null : found.tint ?? null;
    const scalars = found.scalars ?? {};
    // Distortion materials bend the image behind them; the preview leaves them out.
    if (scalars.distortion > 0 && found.blend !== 'additive') { this.isDistortion = true; this.visible = false; }
    if (this.shaderId) return;
    const uniforms = this.material.uniforms;
    uniforms.texturePower.value = scalars.power > 0 ? Math.min(scalars.power, 8) : 1;
    uniforms.strength.value = scalars.strength > 0 ? Math.min(scalars.strength, 4) : 1;
    uniforms.fresnelStrength.value = this.isMesh && scalars.fresnelStrength > 0 ? scalars.fresnelStrength : 0;
    uniforms.fresnelPower.value = scalars.fresnelPower > 0 ? scalars.fresnelPower : 1;
    uniforms.coverage.value = found.blend === 'additive' ? 0 : 1;
    // Mesh particles draw a texture only once their mesh (and so its UVs) is loaded.
    if (this.isMesh && !this.drawMesh) return;
    assets.texture(this.materialName, (texture) => {
      if (this.shaderId || !this.material.uniforms.map) return;
      this.material.uniforms.map.value = texture;
      this.material.uniforms.hasMap.value = 1;
      // Edges matter only for textureless sprites; textures carry their own shape.
      this.material.uniforms.roundEdge.value = this.isMesh ? 0 : 1;
    });
  }

  dispose() {
    this.object.removeFromParent();
    this.geometry.dispose();
    this.material.dispose();
  }
}

// ---- One particle system instance ----
export class ParticleEffect {
  // parent: the Object3D the effect follows (null for a fixed effect at the scene origin); root: where world-space
  // particles are drawn.
  // ground: the world height particles placed on the ground sit at.
  constructor({ model, root, parent = null, assets = null, recipe = defaultRecipe(), parameters = null, ground = 0 }) {
    this.model = model;
    this.ground = ground;
    this.parent = parent;
    this.assets = assets;
    this.recipe = recipe;
    this.scales = parameters ? effectScales(parameters) : NO_SCALES;
    this.group = new THREE.Group();
    (parent ?? root).add(this.group);
    this.worldGroup = parent ? new THREE.Group() : this.group;
    if (parent) root.add(this.worldGroup);
    this.time = 0;
    this.active = true;
    this.emitters = model.emitters.map((emitter) => new Emitter(emitter, this));
    this.onAssets = () => this.emitters.forEach((emitter) => emitter.applyAssets());
    assets?.listeners.add(this.onAssets);
  }

  get finishedAt() {
    const ends = this.emitters.map((emitter) => emitter.finishedAt);
    const finite = ends.filter(Number.isFinite);
    return finite.length === ends.length && finite.length ? Math.max(...finite) : 3;
  }

  get alive() { return this.emitters.some((emitter) => emitter.particles.length > 0); }

  get particleCount() { return this.emitters.reduce((n, e) => n + (e.visible ? e.particles.length : 0), 0); }

  applyRecipe(recipe) {
    this.recipe = recipe;
    this.emitters.forEach((emitter) => emitter.applyRecipe(recipe));
  }

  // Stops spawning; particles that would never die are cleared, the rest live out their lives.
  deactivate() {
    this.active = false;
    for (const emitter of this.emitters) emitter.keep((p) => Number.isFinite(p.life));
  }

  reset() {
    this.time = 0;
    this.active = true;
    this.emitters.forEach((emitter) => emitter.reset());
  }

  update(dt) {
    this.time += dt;
    this.emitters.forEach((emitter) => emitter.update(dt, this.time, this.active));
  }

  write() { this.emitters.forEach((emitter) => emitter.write()); }

  dispose() {
    this.assets?.listeners.delete(this.onAssets);
    this.emitters.forEach((emitter) => emitter.dispose());
    this.group.removeFromParent();
    this.worldGroup.removeFromParent();
  }
}
