// Arkive's 3D map: a whole map from the Zone Viewer's scene (scene.json + scene.bin + DDS textures in the game's own
// compressed formats), drawn as instanced meshes and terrain tiles under the map's own sun, height fog and colour
// grading. Clicking a piece tells the host which mesh and material it is; the host sends back which parts are
// selected (highlighted) and the recolour being set up, which the materials' shaders preview live.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const host = window.chrome?.webview;
const post = (message) => host?.postMessage(message);
const canvas = document.getElementById('view');
const status = document.getElementById('status');
const environmentSelect = document.getElementById('environmentSelect');
const arenaSelect = document.getElementById('arenaSelect');

const renderer = new THREE.WebGLRenderer({ canvas, antialias: false, powerPreference: 'high-performance' });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
const scene = new THREE.Scene();
// Skies and backdrops draw first, on their own: they are the background whatever their depth.
const skyScene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(55, 1, 0.5, 60000);
const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.screenSpacePanning = true;
const ambient = new THREE.HemisphereLight(0xffffff, 0x444444, 1);
const sun = new THREE.DirectionalLight(0xffffff, 2);
// The sun casts shadows over the area being looked at (refitted whenever the view is framed).
sun.castShadow = true;
sun.shadow.mapSize.set(4096, 4096);
sun.shadow.bias = -0.0004;
sun.shadow.normalBias = 0.6;
scene.add(ambient, sun, sun.target);
const pmrem = new THREE.PMREMGenerator(renderer);
let cubemaps = {};
const cubemapCache = new Map();
const s3tc = renderer.extensions.has('WEBGL_compressed_texture_s3tc');
const rgtc = renderer.extensions.has('EXT_texture_compression_rgtc');

// ---- Post-processing: the scene renders into a floating-point target, then one pass adds the map's height fog,
// its colour grading (shadows, mid-tones, highlights, desaturation), tone mapping and its look-up table.
const target = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, samples: 4 });
target.depthTexture = new THREE.DepthTexture(1, 1, THREE.FloatType);
const finish = new THREE.ShaderMaterial({
  uniforms: {
    tColor: { value: target.texture }, tDepth: { value: target.depthTexture }, tLut: { value: null }, hasLut: { value: 0 },
    projectionInverse: { value: new THREE.Matrix4() }, cameraWorld: { value: new THREE.Matrix4() },
    fogColour: { value: new THREE.Color() }, fogDensity: { value: 0 }, fogFalloff: { value: 0 }, fogStart: { value: 0 },
    fogMax: { value: 0 }, fogBase: { value: 0 }, shadows: { value: new THREE.Vector3() }, midtones: { value: new THREE.Vector3(1, 1, 1) },
    highlights: { value: new THREE.Vector3(1, 1, 1) }, desaturation: { value: 0 }, exposure: { value: 1 },
  },
  vertexShader: 'varying vec2 vUv; void main() { vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }',
  fragmentShader: `
    uniform sampler2D tColor, tDepth, tLut; uniform float hasLut;
    uniform mat4 projectionInverse, cameraWorld;
    uniform vec3 fogColour; uniform float fogDensity, fogFalloff, fogStart, fogMax, fogBase;
    uniform vec3 shadows, midtones, highlights; uniform float desaturation, exposure;
    varying vec2 vUv;
    vec3 toSrgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c)); }
    // The game's 256x16 table: 16 slices of 16x16, blue choosing the slice, red across, green down.
    vec3 grade(vec3 c) {
      c = clamp(c, 0.0, 1.0);
      float blue = c.b * 15.0; float first = floor(blue); float second = min(first + 1.0, 15.0);
      vec2 a = vec2((first * 16.0 + c.r * 15.0 + 0.5) / 256.0, (c.g * 15.0 + 0.5) / 16.0);
      vec2 b = vec2((second * 16.0 + c.r * 15.0 + 0.5) / 256.0, (c.g * 15.0 + 0.5) / 16.0);
      return mix(texture2D(tLut, a).rgb, texture2D(tLut, b).rgb, blue - first);
    }
    vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }
    void main() {
      vec3 colour = texture2D(tColor, vUv).rgb * exposure;
      float depth = texture2D(tDepth, vUv).x;
      if (fogDensity > 0.0 && depth < 1.0) {
        vec4 view = projectionInverse * vec4(vUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
        vec3 position = view.xyz / view.w;
        vec3 world = (cameraWorld * vec4(position, 1.0)).xyz;
        float travelled = max(length(position) - fogStart, 0.0);
        float height = exp(-fogFalloff * max(world.y - fogBase, -50.0) * 0.05);
        float amount = min(1.0 - exp(-fogDensity * 0.003 * travelled * height), fogMax);
        colour = mix(colour, fogColour, amount);
      }
      // As Unreal 3 grades a frame: tone map, then on the display colour desaturate, lift the shadows, scale the
      // highlights and bend the mid-tones (an inverse gamma), then the look-up table.
      colour = toSrgb(aces(colour));
      float grey = dot(colour, vec3(0.3, 0.59, 0.11));
      colour = mix(colour, vec3(grey), desaturation);
      colour = pow(clamp((colour - shadows) * highlights, 0.0, 1.0), 1.0 / max(midtones, vec3(0.01)));
      if (hasLut > 0.5) colour = grade(colour);
      gl_FragColor = vec4(colour, 1.0);
    }`,
  depthTest: false, depthWrite: false,
});
const finishScene = new THREE.Scene();
const triangle = new THREE.BufferGeometry();
triangle.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 3, -1, 0, -1, 3, 0], 3));
triangle.setAttribute('uv', new THREE.Float32BufferAttribute([0, 0, 2, 0, 0, 2], 2));
finishScene.add(new THREE.Mesh(triangle, finish));
const finishCamera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);

// ---- Shared shader code: sRGB decoding (textures stay as stored) and the Texture Viewer's recolour rule
// (TextureRecolour and ColourSelections in Arkive.Core), so the recolour previews on any texture.
const shaderCommon = `
  uniform float uTintOn, uSelection, uHue, uWidth, uStrength, uBrightness; uniform vec3 uTarget;
  vec3 srgbToLinear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c)); }
  vec3 toSrgbApprox(vec3 c) { return pow(max(c, 0.0), vec3(1.0 / 2.2)); }
  float tintHue(vec3 c) {
    float high = max(c.r, max(c.g, c.b)); float delta = high - min(c.r, min(c.g, c.b));
    if (delta <= 0.0) return 0.0;
    float h = high == c.r ? mod((c.g - c.b) / delta, 6.0) : high == c.g ? (c.b - c.r) / delta + 2.0 : (c.r - c.g) / delta + 4.0;
    return mod(h * 60.0 + 360.0, 360.0);
  }
  float tintRamp(float value, float from, float to) { return clamp((value - from) / (to - from), 0.0, 1.0); }
  float tintWeight(vec3 c) {
    if (uSelection < 0.5) return 1.0;
    float high = max(c.r, max(c.g, c.b)); float low = min(c.r, min(c.g, c.b));
    if (high <= 0.0) return 0.0;
    float saturation = (high - low) / high;
    if (uSelection > 1.5) return tintRamp(0.25 - saturation, 0.0, 0.12) * tintRamp(high, 0.5, 0.75);
    float distance = abs(mod(tintHue(c) - uHue + 540.0, 360.0) - 180.0);
    return tintRamp(uWidth + 12.0 - distance, 0.0, 12.0) * tintRamp(saturation, 0.08, 0.2);
  }
  vec3 recolour(vec3 c, float on) {
    if (on < 0.5) return c;
    float amount = tintWeight(c);
    if (amount <= 0.0) return c;
    float high = max(c.r, max(c.g, c.b));
    float peak = max(uTarget.r, max(uTarget.g, uTarget.b));
    vec3 tinted = high * uTarget / max(peak, 0.0001);
    vec3 changed = (c + (tinted - c) * uStrength) * (1.0 + uBrightness);
    return c + (changed - c) * amount;
  }`;

const tintUniforms = () => ({
  uTintOn: { value: 0 }, uSelection: { value: 0 }, uHue: { value: 45 }, uWidth: { value: 25 }, uStrength: { value: 0 },
  uBrightness: { value: 0 }, uTarget: { value: new THREE.Vector3(1, 1, 1) },
});
let currentTint = null;
function setTint(uniforms, on) {
  uniforms.uTintOn.value = on && currentTint ? 1 : 0;
  if (!currentTint) return;
  uniforms.uSelection.value = { All: 0, ColourRange: 1, LightAreas: 2 }[currentTint.selection] ?? 0;
  uniforms.uHue.value = currentTint.rangeHue; uniforms.uWidth.value = currentTint.rangeWidth;
  uniforms.uStrength.value = currentTint.strength; uniforms.uBrightness.value = currentTint.brightness;
  uniforms.uTarget.value.set(currentTint.red, currentTint.green, currentTint.blue);
}

const linear = (rgb) => new THREE.Color().setRGB(rgb[0], rgb[1], rgb[2], THREE.SRGBColorSpace);

function meshMaterial(item, textures) {
  const uniforms = tintUniforms();
  const diffuse = item.diffuse >= 0 ? textures[item.diffuse] : null;
  const glow = item.emissive >= 0 ? textures[item.emissive] : null;
  // The texture's colour times the material's tint; without a texture, its average colour (or grey).
  const normal = item.normal >= 0 ? textures[item.normal] : null;
  const material = new THREE.MeshStandardMaterial({ side: THREE.DoubleSide, color: new THREE.Color(item.tint[0], item.tint[1], item.tint[2]),
    roughness: item.roughness ?? 0.85, metalness: item.metalness ?? 0 });
  if (normal) {
    material.normalMap = normal.clone();
    material.normalMap.repeat.set(item.tiling, item.tiling);
    // Unreal's normal maps point green down.
    material.normalScale = new THREE.Vector2(item.normalStrength ?? 1, -(item.normalStrength ?? 1));
  }
  if (diffuse) {
    material.map = diffuse.clone();
    material.map.repeat.set(item.tiling, item.tiling);
    material.map.needsUpdate = true;
  } else material.color.multiply(linear(item.colour));
  if (glow) {
    material.emissiveMap = glow.clone();
    material.emissiveMap.repeat.set(item.tiling, item.tiling);
    material.emissive = new THREE.Color(item.emissiveColour[0], item.emissiveColour[1], item.emissiveColour[2]);
  }
  if (item.masked && diffuse) material.alphaTest = 0.5;
  material.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms);
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', '#include <common>\n' + shaderCommon)
      .replace('#include <map_fragment>', `
        #ifdef USE_MAP
          vec4 texel = texture2D(map, vMapUv);
          diffuseColor.rgb *= srgbToLinear(recolour(texel.rgb, uTintOn));
          diffuseColor.a *= texel.a;
        #else
          diffuseColor.rgb = srgbToLinear(recolour(toSrgbApprox(diffuseColor.rgb), uTintOn));
        #endif`)
      .replace('#include <emissivemap_fragment>', `
        #ifdef USE_EMISSIVEMAP
          totalEmissiveRadiance *= srgbToLinear(recolour(texture2D(emissiveMap, vEmissiveMapUv).rgb, uTintOn));
        #endif`)
      // The game's normal maps keep only x and y (two-channel compression); rebuild z.
      .replace('vec3 mapN = texture2D( normalMap, vNormalMapUv ).xyz * 2.0 - 1.0;',
        'vec3 mapN = vec3(texture2D( normalMap, vNormalMapUv ).xy * 2.0 - 1.0, 0.0); mapN.z = sqrt(max(0.0, 1.0 - dot(mapN.xy, mapN.xy)));');
  };
  return { material, uniforms };
}

// Skies, backdrops and planets are painted, not lit: their colour texture (dimmed, as the game lights it faintly)
// plus their glow, blended by the texture's alpha, behind everything and out of the fog.
function skyMaterial(item, textures) {
  const uniforms = tintUniforms();
  const diffuse = item.diffuse >= 0 ? textures[item.diffuse] : null;
  const glow = item.emissive >= 0 ? textures[item.emissive] : null;
  const material = new THREE.MeshBasicMaterial({ side: THREE.DoubleSide, depthWrite: false, fog: false, transparent: true });
  const first = diffuse ?? glow;
  if (!first) return { material: null, uniforms };
  material.map = first.clone();
  material.map.repeat.set(item.tiling, item.tiling);
  const second = diffuse && glow ? glow.clone() : null;
  if (second) second.repeat.set(item.tiling, item.tiling);
  // Sky materials often push their colours far past white (the game grades them back down); keep the hue, cap the strength.
  const capped = (rgb, most) => { const peak = Math.max(...rgb); return new THREE.Vector3(...rgb).multiplyScalar(peak > most ? most / peak : 1); };
  const tint = capped(item.tint, 2).multiplyScalar(diffuse ? 0.35 : 0);
  const shine = capped(item.emissiveColour, 2);
  Object.assign(uniforms, { uSkyTint: { value: tint }, uSkyGlow: { value: shine }, uSkySecond: { value: second ?? first },
    uSkyFirstGlows: { value: diffuse ? 0 : 1 } });
  material.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms);
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', '#include <common>\n' + shaderCommon + '\nuniform vec3 uSkyTint, uSkyGlow; uniform sampler2D uSkySecond; uniform float uSkyFirstGlows;')
      .replace('#include <map_fragment>', `
        vec4 texel = texture2D(map, vMapUv);
        vec3 lit = srgbToLinear(recolour(texel.rgb, uTintOn));
        vec4 glowTexel = uSkyFirstGlows > 0.5 ? texel : texture2D(uSkySecond, vMapUv);
        vec3 shining = srgbToLinear(recolour(glowTexel.rgb, uTintOn));
        diffuseColor.rgb = lit * uSkyTint + shining * uSkyGlow;
        diffuseColor.a = uSkyFirstGlows > 0.5 ? texel.a : max(texel.a, glowTexel.a);`);
  };
  return { material, uniforms };
}

// A terrain tile mixes up to eight ground layers by the weights its blend masks give each vertex.
function terrainMaterial(layers, textures) {
  const uniforms = tintUniforms();
  const maps = [], tints = [], tilings = [], tinted = [];
  for (let index = 0; index < 8; index++) {
    const layer = layers[index];
    maps.push(layer && layer.texture >= 0 ? textures[layer.texture] : textures.white);
    tints.push(layer ? new THREE.Vector3(...layer.tint) : new THREE.Vector3());
    tilings.push(layer ? layer.tiling : 1);
    tinted.push(0);
  }
  Object.assign(uniforms, { layerMaps: { value: maps }, layerTints: { value: tints }, layerTilings: { value: tilings },
    layerTinted: { value: tinted }, layerCount: { value: Math.min(8, layers.length) } });
  const material = new THREE.MeshStandardMaterial({ side: THREE.DoubleSide, roughness: 0.95, metalness: 0 });
  material.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms);
    shader.vertexShader = shader.vertexShader
      .replace('#include <common>', '#include <common>\nattribute vec4 weightsA, weightsB; attribute vec2 terrainUv; varying vec4 vWeightsA, vWeightsB; varying vec2 vTerrainUv;')
      .replace('#include <begin_vertex>', '#include <begin_vertex>\nvWeightsA = weightsA; vWeightsB = weightsB; vTerrainUv = terrainUv;');
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', `#include <common>\n${shaderCommon}
        uniform sampler2D layerMaps[8]; uniform vec3 layerTints[8]; uniform float layerTilings[8]; uniform float layerTinted[8]; uniform int layerCount;
        varying vec4 vWeightsA, vWeightsB; varying vec2 vTerrainUv;`)
      .replace('#include <map_fragment>', `
        float weights[8] = float[8](vWeightsA.x, vWeightsA.y, vWeightsA.z, vWeightsA.w, vWeightsB.x, vWeightsB.y, vWeightsB.z, vWeightsB.w);
        vec3 ground = vec3(0.0); float total = 0.0;
        #pragma unroll_loop_start
        for (int i = 0; i < 8; i++) {
          if (i < layerCount && weights[i] > 0.0) {
            vec3 texel = texture2D(layerMaps[i], vTerrainUv * layerTilings[i]).rgb;
            ground += weights[i] * srgbToLinear(recolour(texel, layerTinted[i] * uTintOn)) * layerTints[i];
            total += weights[i];
          }
        }
        #pragma unroll_loop_end
        diffuseColor.rgb *= total > 0.0 ? ground / total : vec3(0.4);`);
  };
  return { material, uniforms };
}

// ---- Baked light: the levels' lightmaps, packed into pages, light the static pieces as the game does (their
// sunlight stays live, with its shadows). A copy of a material per page samples its page at each placement's spot.
const baked = { on: { value: 1 }, strength: { value: 9 }, gamma: { value: 1.5 } };
const bakedCopies = new Map();
function bakedMaterial(index, page, textures) {
  const key = `${index}|${page}`;
  if (!bakedCopies.has(key)) {
    const source = materials[index].material;
    const copy = source.clone();
    copy.defines = { ...(copy.defines ?? {}), USE_BAKED: '' };
    const map = textures[description.lightmaps[page]];
    copy.onBeforeCompile = (shader) => {
      source.onBeforeCompile(shader);
      Object.assign(shader.uniforms, { uBaked: { value: map }, uBakedOn: baked.on, uBakedStrength: baked.strength, uBakedGamma: baked.gamma });
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', '#include <common>\nattribute vec2 bakedUv; attribute vec4 bakedPlace; varying vec2 vBakedUv;')
        .replace('#include <uv_vertex>', '#include <uv_vertex>\nvBakedUv = bakedUv * bakedPlace.xy + bakedPlace.zw;');
      shader.fragmentShader = shader.fragmentShader
        .replace('#include <common>', '#include <common>\nuniform sampler2D uBaked; uniform float uBakedOn, uBakedStrength, uBakedGamma; varying vec2 vBakedUv;')
        // The baked light replaces the sky's fill light and the cubemap's diffuse light; reflections stay.
        .replace('#include <lights_fragment_maps>', `#include <lights_fragment_maps>
          if (uBakedOn > 0.5) {
            irradiance = pow(texture2D(uBaked, vBakedUv).rgb, vec3(uBakedGamma)) * uBakedStrength * PI;
            iblIrradiance = vec3(0.0);
          }`);
    };
    bakedCopies.set(key, copy);
  }
  return bakedCopies.get(key);
}

// ---- Textures: DDS files in the game's formats (DXT1, DXT5, ATI2) or plain BGRA.

function loadTexture(buffer, item) {
  const view = new DataView(buffer);
  const mipCount = Math.max(1, view.getUint32(28, true));
  let width = item.width, height = item.height, offset = 128;
  const block = { DXT1: 8, DXT5: 16, ATI2: 16 }[item.format];
  const mipmaps = [];
  for (let level = 0; level < mipCount && offset < buffer.byteLength; level++) {
    const size = block ? Math.max(1, Math.ceil(width / 4)) * Math.max(1, Math.ceil(height / 4)) * block : width * height * 4;
    if (offset + size > buffer.byteLength) break;
    mipmaps.push({ data: new Uint8Array(buffer, offset, size), width, height });
    offset += size; width = Math.max(1, width >> 1); height = Math.max(1, height >> 1);
  }
  let texture;
  if (block) {
    if ((item.format === 'ATI2' && !rgtc) || (item.format !== 'ATI2' && !s3tc)) return null;
    const format = item.format === 'DXT1' ? THREE.RGBA_S3TC_DXT1_Format : item.format === 'DXT5' ? THREE.RGBA_S3TC_DXT5_Format : THREE.RED_GREEN_RGTC2_Format;
    texture = new THREE.CompressedTexture(mipmaps, item.width, item.height, format);
  } else {
    for (const mip of mipmaps) {
      const rgba = new Uint8Array(mip.data.length);
      for (let index = 0; index < rgba.length; index += 4) {
        rgba[index] = mip.data[index + 2]; rgba[index + 1] = mip.data[index + 1]; rgba[index + 2] = mip.data[index]; rgba[index + 3] = mip.data[index + 3];
      }
      mip.data = rgba;
    }
    texture = new THREE.DataTexture(mipmaps[0].data, item.width, item.height, THREE.RGBAFormat);
    texture.mipmaps = mipmaps;
  }
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.magFilter = THREE.LinearFilter;
  texture.minFilter = mipmaps.length > 1 ? THREE.LinearMipmapLinearFilter : THREE.LinearFilter;
  texture.generateMipmaps = false;
  texture.anisotropy = Math.min(4, renderer.capabilities.getMaxAnisotropy());
  texture.needsUpdate = true;
  return texture;
}

// ---- Scene ----

const highlight = new THREE.Color(1.0, 0.72, 0.42);
const white = new THREE.Color(1, 1, 1);
let description = null;
let materials = [];        // { key, textureKey, glowKey, material, uniforms }
let batches = [];          // { mesh: InstancedMesh, meshKey, layer, slotKeys[] }
let tiles = [];            // terrain meshes; userData.tile = { materialKey, layers[], uniforms }
let selected = new Set();
const box = new THREE.Box3Helper(new THREE.Box3(), 0xffa040);
box.visible = false;
scene.add(box);

const bakedCheck = document.getElementById('bakedCheck');
bakedCheck.addEventListener('change', () => { baked.on.value = bakedCheck.checked ? 1 : 0; });
const layerChecks = { sky: document.getElementById('skyCheck'), effect: document.getElementById('effectCheck'), cutscene: document.getElementById('cutsceneCheck') };
const layerVisible = (layer) => layerChecks[layer]?.checked ?? true;
for (const check of Object.values(layerChecks))
  check.addEventListener('change', () => { for (const batch of batches) batch.mesh.visible = layerVisible(batch.layer); });

function resize() {
  const width = Math.max(1, canvas.clientWidth), height = Math.max(1, canvas.clientHeight);
  renderer.setSize(width, height, false);
  const ratio = renderer.getPixelRatio();
  target.setSize(Math.floor(width * ratio), Math.floor(height * ratio));
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
}
window.addEventListener('resize', resize);

async function load(sceneUrl, baseUrl) {
  status.textContent = 'Loading the map…';
  description = await (await fetch(sceneUrl)).json();
  const data = await (await fetch(baseUrl + 'scene.bin')).arrayBuffer();
  try { cubemaps = await (await fetch(baseUrl + 'cubemaps.json')).json(); } catch { cubemaps = {}; }
  const textures = await Promise.all(description.textures.map(async (item) => {
    try { return loadTexture(await (await fetch(baseUrl + item.file)).arrayBuffer(), item); } catch { return null; }
  }));
  // Pages of baked light are sampled at exact spots: no repeats, no smaller levels to blend neighbours together.
  for (const index of description.lightmaps ?? []) {
    const page = textures[index];
    if (!page) continue;
    page.wrapS = page.wrapT = THREE.ClampToEdgeWrapping;
    page.minFilter = THREE.LinearFilter;
    page.anisotropy = 1;
  }
  textures.white = new THREE.DataTexture(new Uint8Array([200, 200, 200, 255]), 1, 1);
  textures.white.needsUpdate = true;
  const textureKeys = description.textures.map((item) => item.key);
  materials = description.materials.map((item) => {
    const usable = { ...item, diffuse: item.diffuse >= 0 && textures[item.diffuse] ? item.diffuse : -1, emissive: item.emissive >= 0 && textures[item.emissive] ? item.emissive : -1 };
    const usableNormal = { ...usable, normal: item.normal >= 0 && textures[item.normal] ? item.normal : -1 };
    const { material, uniforms } = meshMaterial(usableNormal, textures);
    const sky = skyMaterial(usable, textures);
    return { key: item.key.toLowerCase(), textureKey: item.diffuse >= 0 ? textureKeys[item.diffuse] : null,
      glowKey: item.emissive >= 0 ? textureKeys[item.emissive] : null, material, uniforms, sky: sky.material, skyUniforms: sky.uniforms };
  });
  const fallback = new THREE.MeshLambertMaterial({ color: 0x8c8c8c, side: THREE.DoubleSide });
  // Normals are four signed bytes a vertex (the fourth unused).
  const geometries = description.meshes.map((mesh) => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(data, mesh.positions.offset, mesh.positions.count * 3), 3));
    if (mesh.normals) {
      const packed = new Int8Array(data, mesh.normals.offset, mesh.normals.count * 4);
      geometry.setAttribute('normal', new THREE.InterleavedBufferAttribute(new THREE.InterleavedBuffer(packed, 4), 3, 0, true));
    }
    geometry.setAttribute('uv', new THREE.Float16BufferAttribute(new Uint16Array(data, mesh.uvs.offset, mesh.uvs.count * 2), 2));
    if (mesh.lightmapUvs) geometry.setAttribute('bakedUv', new THREE.Float16BufferAttribute(new Uint16Array(data, mesh.lightmapUvs.offset, mesh.lightmapUvs.count * 2), 2));
    geometry.setIndex(new THREE.BufferAttribute(new Uint16Array(data, mesh.indices.offset, mesh.indices.count), 1));
    for (const section of mesh.sections) geometry.addGroup(section.firstIndex, section.indexCount, section.material);
    if (!mesh.normals) geometry.computeVertexNormals();
    geometry.computeBoundingSphere();
    return geometry;
  });
  const matrix = new THREE.Matrix4();
  // Flat pieces with an alpha channel (sand drifts, puddles, ground decals) blend into what is under them.
  const decalCopies = new Map();
  const decal = (index) => {
    if (!decalCopies.has(index)) {
      const copy = materials[index].material.clone();
      copy.onBeforeCompile = materials[index].material.onBeforeCompile;
      copy.transparent = true; copy.depthWrite = false; copy.alphaTest = 0;
      copy.polygonOffset = true; copy.polygonOffsetFactor = -2;
      decalCopies.set(index, copy);
    }
    return decalCopies.get(index);
  };
  for (const batch of description.batches) {
    let geometry = geometries[batch.mesh];
    geometry.computeBoundingBox();
    const page = batch.lightmap ?? -1;
    const lit = page >= 0 && batch.lightmapPlaces && geometry.attributes.bakedUv && textures[description.lightmaps[page]];
    if (lit) {
      // The mesh's own buffers, plus where each placement sits on its page of baked light.
      const view = new THREE.BufferGeometry();
      for (const [name, attribute] of Object.entries(geometry.attributes)) view.setAttribute(name, attribute);
      view.setIndex(geometry.index);
      for (const group of geometry.groups) view.addGroup(group.start, group.count, group.materialIndex);
      view.boundingBox = geometry.boundingBox;
      view.boundingSphere = geometry.boundingSphere;
      view.setAttribute('bakedPlace', new THREE.InstancedBufferAttribute(new Float32Array(data, batch.lightmapPlaces.offset, batch.lightmapPlaces.count * 4), 4));
      geometry = view;
    }
    const extent = geometry.boundingBox.getSize(new THREE.Vector3());
    const flat = Math.min(extent.x, extent.y, extent.z) < Math.max(extent.x, extent.y, extent.z) * 0.03;
    const isSky = batch.layer === 'sky';
    // Sky pieces whose look the material builds without textures are left out: drawn flat they would be sheets.
    if (isSky && batch.materials.every((index) => index < 0 || !materials[index].sky)) continue;
    const slots = batch.materials.map((index) => index < 0 ? fallback
      : isSky ? materials[index].sky ?? fallback
      : flat && description.materials[index].diffuse >= 0 && description.textures[description.materials[index].diffuse].format !== 'DXT1' ? decal(index)
      : lit ? bakedMaterial(index, page, textures)
      : materials[index].material);
    const mesh = new THREE.InstancedMesh(geometry, slots.length ? slots : fallback, batch.matrices.count);
    const values = new Float32Array(data, batch.matrices.offset, batch.matrices.count * 16);
    for (let index = 0; index < batch.matrices.count; index++) {
      matrix.fromArray(values, index * 16);
      mesh.setMatrixAt(index, matrix);
      mesh.setColorAt(index, white);
    }
    mesh.instanceMatrix.needsUpdate = true;
    mesh.computeBoundingSphere();
    if (isSky) { mesh.renderOrder = -10; mesh.frustumCulled = false; } else { mesh.castShadow = true; mesh.receiveShadow = true; }
    const meshKey = description.meshes[batch.mesh].key.toLowerCase();
    const entry = { mesh, meshKey, layer: batch.layer ?? 'solid', slotKeys: batch.materials.map((index) => index >= 0 ? materials[index].key : null),
      matrices: values };
    mesh.visible = layerVisible(entry.layer);
    mesh.userData.batch = entry;
    batches.push(entry);
    (isSky ? skyScene : scene).add(mesh);
  }
  for (const tile of description.terrain) {
    const geometry = new THREE.BufferGeometry();
    const count = tile.positions.count;
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(data, tile.positions.offset, count * 3), 3));
    geometry.setAttribute('terrainUv', new THREE.Float16BufferAttribute(new Uint16Array(data, tile.uvs.offset, count * 2), 2));
    geometry.setIndex(new THREE.BufferAttribute(new Uint16Array(data, tile.indices.offset, tile.indices.count), 1));
    geometry.computeVertexNormals();
    const layers = tile.layers ?? [];
    const a = new Float32Array(count * 4), b = new Float32Array(count * 4);
    if (tile.weights && layers.length) {
      const weights = new Uint8Array(data, tile.weights.offset, tile.weights.count);
      for (let vertex = 0; vertex < count; vertex++)
        for (let layer = 0; layer < Math.min(8, layers.length); layer++)
          (layer < 4 ? a : b)[vertex * 4 + (layer % 4)] = weights[vertex * layers.length + layer] / 255;
    } else for (let vertex = 0; vertex < count; vertex++) a[vertex * 4] = 1;
    geometry.setAttribute('weightsA', new THREE.BufferAttribute(a, 4));
    geometry.setAttribute('weightsB', new THREE.BufferAttribute(b, 4));
    const own = tile.material >= 0 ? description.materials[tile.material] : null;
    const useLayers = layers.length ? layers : [{ texture: own ? own.diffuse : -1, tint: own ? own.tint : [1, 1, 1], tiling: 1 }];
    const { material, uniforms } = terrainMaterial(useLayers.map((layer) => ({ ...layer, texture: layer.texture >= 0 && textures[layer.texture] ? layer.texture : -1 })), textures);
    const mesh = new THREE.Mesh(geometry, material);
    mesh.castShadow = true; mesh.receiveShadow = true;
    mesh.userData.tile = { materialKey: own ? own.key.toLowerCase() : null, layers: layers.map((layer) => layer.textureKey?.toLowerCase() ?? null), uniforms };
    tiles.push(mesh);
    scene.add(mesh);
  }
  environmentSelect.innerHTML = '';
  description.environments.forEach((environment, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    const name = environment.name === 'map' ? "The map's own" : `Area ${index}`;
    option.textContent = index === description.environment ? `${name} (the play area's)` : name;
    environmentSelect.appendChild(option);
  });
  const plain = document.createElement('option');
  plain.value = 'plain'; plain.textContent = 'Plain light (no fog or grading)';
  environmentSelect.appendChild(plain);
  environmentSelect.value = String(description.environment);
  arenaSelect.innerHTML = '';
  (description.arenas ?? []).forEach((arena, index) => {
    const option = document.createElement('option');
    option.value = String(index);
    option.textContent = index === 0 ? 'Arena 1 (the busiest)' : `Arena ${index + 1}`;
    arenaSelect.appendChild(option);
  });
  arenaSelect.parentElement.style.display = (description.arenas ?? []).length > 1 ? '' : 'none';
  if (description.arenas?.length) await goToArena(0);
  else { await applyEnvironment(baseUrl); frame(); }
  status.textContent = '';
  post({ type: 'loaded', placements: description.placements, meshes: description.meshes.length, terrain: description.terrain.length });
  applySelection();
  applyTint([], null);
}

const lutCache = new Map();
async function applyEnvironment(baseUrl) {
  const plain = environmentSelect.value === 'plain';
  const environment = description.environments[plain ? description.environment : Number(environmentSelect.value)];
  const u = finish.uniforms;
  const direction = new THREE.Vector3(...environment.sunDirection).normalize();
  sun.color.copy(linear(environment.sunColour));
  sun.intensity = Math.max(0.6, environment.sunBrightness) * 1.6;
  sunDirection.copy(direction).negate();
  fitShadows(sun.target.position.clone(), shadowSize);
  ambient.color.copy(linear(environment.ambientColour));
  ambient.groundColor.copy(linear(environment.ambientColour)).multiplyScalar(0.45);
  // The game lights and reflects with its environment cubemap; the hemisphere fills in when it is missing.
  const faces = environment.cubemap ? cubemaps[environment.cubemap] : null;
  scene.environment = faces ? await cubemap(baseUrl, faces) : null;
  ambient.intensity = scene.environment ? 0.35 : 1.4;
  skyScene.background = linear(environment.fogColour).multiplyScalar(0.8);
  u.fogColour.value.copy(linear(environment.fogColour));
  u.fogDensity.value = plain ? 0 : environment.fogDensity;
  u.fogFalloff.value = environment.fogHeightFalloff;
  u.fogStart.value = environment.fogStart;
  u.fogMax.value = environment.fogMaxOpacity;
  u.fogBase.value = description.focus[1];
  u.shadows.value.set(...(plain ? [0, 0, 0] : environment.shadows));
  u.midtones.value.set(...(plain ? [1, 1, 1] : environment.midtones));
  u.highlights.value.set(...(plain ? [1, 1, 1] : environment.highlights));
  u.desaturation.value = plain ? 0 : environment.desaturation;
  u.hasLut.value = 0;
  if (!plain && environment.lut) {
    let lut = lutCache.get(environment.lut);
    if (!lut) {
      lut = await new THREE.TextureLoader().loadAsync(baseUrl + environment.lut);
      lut.magFilter = lut.minFilter = THREE.LinearFilter;
      lut.generateMipmaps = false;
      lut.flipY = false;
      lut.colorSpace = THREE.NoColorSpace;
      lut.needsUpdate = true;
      lutCache.set(environment.lut, lut);
    }
    u.tLut.value = lut;
    u.hasLut.value = 1;
  }
}
async function cubemap(baseUrl, faces) {
  const key = faces.join('|');
  if (!cubemapCache.has(key)) {
    const cube = await new THREE.CubeTextureLoader().loadAsync(faces.map((face) => baseUrl + face));
    cube.colorSpace = THREE.SRGBColorSpace;
    cubemapCache.set(key, pmrem.fromCubemap(cube).texture);
  }
  return cubemapCache.get(key);
}

let base = '';
environmentSelect.addEventListener('change', () => applyEnvironment(base));
// Going to an arena frames it and switches to the lighting that applies there.
arenaSelect.addEventListener('change', () => goToArena(Number(arenaSelect.value)));
function goToArena(index) {
  const arena = description.arenas?.[index];
  if (!arena) { frame(); return Promise.resolve(); }
  currentArena = index;
  restorePieces();
  showSkiesNear(new THREE.Vector3(...arena.centre), Math.max(1500, arena.size * 4));
  if (gameCheck.checked) gameView(arena);
  else look(new THREE.Box3().setFromCenterAndSize(new THREE.Vector3(...arena.centre), new THREE.Vector3(1, 1, 1).multiplyScalar(arena.size * 0.6)));
  environmentSelect.value = String(arena.environment);
  return applyEnvironment(base);
}

// Each area of a map has its own sky dome and backdrops; only those around the place being looked at are drawn.
function showSkiesNear(centre, reach) {
  const matrix = new THREE.Matrix4(), hidden = new THREE.Matrix4().makeScale(0, 0, 0);
  const near = (values, index) => Math.hypot(values[index * 16 + 12] - centre.x, values[index * 16 + 14] - centre.z) < reach;
  const skies = batches.filter((batch) => batch.layer === 'sky');
  const any = skies.some((batch) => Array.from({ length: batch.mesh.count }, (_, index) => near(batch.matrices, index)).some(Boolean));
  for (const batch of skies) {
    for (let index = 0; index < batch.mesh.count; index++)
      batch.mesh.setMatrixAt(index, !any || near(batch.matrices, index) ? matrix.fromArray(batch.matrices, index * 16) : hidden);
    batch.mesh.instanceMatrix.needsUpdate = true;
  }
}

// ---- The game's camera: Lost Ark looks down at the player from a fixed angle, and anything higher than the
// camera never shows; the game view frames an arena that way and cuts away what stands above the camera.
const gameCheck = document.getElementById('gameCheck');
// The game's usual camera, framed from three times its distance to show more of the arena.
const gameDefaults = { yaw: 0, pitch: -50, distance: 22, fov: 50 };
let currentArena = 0;
function floorAt(centre) {
  const raycaster = new THREE.Raycaster(new THREE.Vector3(centre.x, centre.y + 3, centre.z), new THREE.Vector3(0, -1, 0), 0, 200);
  const solid = [...batches.filter((batch) => batch.layer === 'solid').map((batch) => batch.mesh), ...tiles];
  const hits = raycaster.intersectObjects(solid, false);
  if (!hits.length) return centre.y;
  return hits.reduce((best, hit) => Math.abs(hit.point.y - centre.y) < Math.abs(best - centre.y) ? hit.point.y : best, hits[0].point.y);
}
// Seen from the game's camera, skies and backdrops stand where the level puts them, in front of what is behind
// them; looked at from anywhere else they are a background.
function placeSkies(inWorld) {
  for (const batch of batches) if (batch.layer === 'sky') (inWorld ? scene : skyScene).add(batch.mesh);
}

// Like the game, which fades out what comes between its camera and the player: pieces that stand between the camera
// and the arena's floor are hidden while the game camera looks at it.
let hiddenPieces = [];
function restorePieces() {
  const matrix = new THREE.Matrix4();
  for (const { batch, index } of hiddenPieces) {
    batch.mesh.setMatrixAt(index, matrix.fromArray(batch.matrices, index * 16));
    batch.mesh.instanceMatrix.needsUpdate = true;
  }
  hiddenPieces = [];
}
function hideOccluders(target) {
  const hidden = new THREE.Matrix4().makeScale(0, 0, 0);
  const solid = batches.filter((batch) => batch.layer !== 'sky' && batch.mesh.visible).map((batch) => batch.mesh);
  const raycaster = new THREE.Raycaster();
  camera.updateMatrixWorld();
  const reach = camera.position.distanceTo(target);
  for (let pass = 0; pass < 3; pass++) {
    let found = false;
    for (let x = -0.5; x <= 0.5; x += 0.25)
      for (let y = -0.45; y <= 0.45; y += 0.3) {
        raycaster.setFromCamera(new THREE.Vector2(x, y), camera);
        raycaster.far = reach * 1.2;
        for (const mesh of solid) mesh.updateMatrixWorld();
        const hit = raycaster.intersectObjects(solid, false)[0];
        // Close to the camera and well above the floor: something in the way.
        if (!hit || hit.instanceId === undefined || hit.point.y < target.y + 8 || hit.distance > reach * 0.6) continue;
        const batch = hit.object.userData.batch;
        hit.object.setMatrixAt(hit.instanceId, hidden);
        hit.object.instanceMatrix.needsUpdate = true;
        hit.object.computeBoundingSphere();
        hiddenPieces.push({ batch, index: hit.instanceId });
        found = true;
      }
    if (!found) break;
  }
}

function gameView(arena) {
  const view = gameDefaults;
  const centre = new THREE.Vector3(...arena.centre);
  const target = new THREE.Vector3(centre.x, floorAt(centre), centre.z);
  const yaw = THREE.MathUtils.degToRad(view.yaw), pitch = THREE.MathUtils.degToRad(view.pitch);
  // Unreal's forward vector for the rotation, in the viewer's axes (Unreal's Y is the viewer's Z).
  const forward = new THREE.Vector3(Math.cos(pitch) * Math.cos(yaw), Math.sin(pitch), Math.cos(pitch) * Math.sin(yaw));
  const distance = view.distance * 3;
  fitShadows(target, Math.max(80, distance * 3));
  controls.target.copy(target);
  camera.position.copy(target).addScaledVector(forward, -distance);
  camera.fov = view.fov;
  camera.near = 0.3;
  camera.far = 40000;
  camera.updateProjectionMatrix();
  controls.update();
  renderer.clippingPlanes = [new THREE.Plane(new THREE.Vector3(0, -1, 0), target.y + distance * Math.sin(-pitch) - 2)];
  placeSkies(true);
  hideOccluders(target);
}
gameCheck.addEventListener('change', () => goToArena(currentArena));

function frame() {
  look(new THREE.Box3().setFromCenterAndSize(new THREE.Vector3(...description.focus), new THREE.Vector3(1, 1, 1).multiplyScalar(description.focusSize * 0.6)));
}

// The sun shines from sunDirection (towards the light); its shadows cover the area being looked at.
const sunDirection = new THREE.Vector3(0.4, 1, 0.3).normalize();
let shadowSize = 100;
function fitShadows(centre, size) {
  shadowSize = size;
  sun.target.position.copy(centre);
  sun.position.copy(centre).addScaledVector(sunDirection, size * 2);
  const half = size * 0.75;
  Object.assign(sun.shadow.camera, { left: -half, right: half, top: half, bottom: -half, near: 1, far: size * 5 });
  sun.shadow.camera.updateProjectionMatrix();
  sun.target.updateMatrixWorld();
}

function look(bounds) {
  renderer.clippingPlanes = [];
  restorePieces();
  placeSkies(false);
  camera.fov = 55;
  const centre = bounds.getCenter(new THREE.Vector3());
  const size = Math.max(10, bounds.getSize(new THREE.Vector3()).length());
  fitShadows(centre, size);
  controls.target.copy(centre);
  camera.position.copy(centre).add(new THREE.Vector3(0.55, 0.5, 0.75).normalize().multiplyScalar(size * 0.9));
  camera.near = Math.max(0.3, size / 2000);
  // Far enough for the sky domes and backdrops around the map.
  camera.far = Math.max(40000, size * 40);
  camera.updateProjectionMatrix();
  controls.update();
}

// ---- Selection ----

const matches = (key) => key && selected.has(key);

function applySelection() {
  for (const batch of batches) {
    const on = matches(batch.meshKey) || batch.slotKeys.some((key) => {
      const material = materials.find((item) => item.key === key);
      return matches(key) || matches(material?.textureKey) || matches(material?.glowKey);
    });
    batch.on = on;
    for (let index = 0; index < batch.mesh.count; index++) batch.mesh.setColorAt(index, on ? highlight : white);
    batch.mesh.instanceColor.needsUpdate = true;
  }
  for (const tile of tiles) {
    const info = tile.userData.tile;
    const on = matches(info.materialKey) || info.layers.some(matches);
    tile.material.emissive?.set(on ? 0x1a1206 : 0x000000);
  }
}

let down = null;
canvas.addEventListener('pointerdown', (event) => { down = { x: event.clientX, y: event.clientY }; });
canvas.addEventListener('pointerup', (event) => {
  if (!down || Math.hypot(event.clientX - down.x, event.clientY - down.y) > 4 || event.button !== 0) return;
  const rect = canvas.getBoundingClientRect();
  const pointer = new THREE.Vector2(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
  const raycaster = new THREE.Raycaster();
  raycaster.setFromCamera(pointer, camera);
  // The sky is behind everything, so it is picked only where nothing else is.
  const hits = raycaster.intersectObjects([...batches.filter((batch) => batch.mesh.visible).map((batch) => batch.mesh), ...tiles], false);
  const hit = hits.find((item) => item.object.userData.batch?.layer !== 'sky') ?? hits[0];
  if (!hit) { box.visible = false; return; }
  if (hit.object.userData.batch) {
    const batch = hit.object.userData.batch;
    const group = hit.object.geometry.groups.find((item) => hit.faceIndex * 3 >= item.start && hit.faceIndex * 3 < item.start + item.count);
    const matrix = new THREE.Matrix4();
    batch.mesh.getMatrixAt(hit.instanceId, matrix);
    hit.object.geometry.computeBoundingBox();
    box.box.copy(hit.object.geometry.boundingBox).applyMatrix4(matrix);
    box.visible = true;
    const material = batch.slotKeys[group?.materialIndex ?? 0];
    post({ type: 'pick', mesh: batch.meshKey, material, texture: materials.find((item) => item.key === material)?.textureKey ?? null });
  } else {
    const info = hit.object.userData.tile;
    hit.object.geometry.computeBoundingBox();
    box.box.copy(hit.object.geometry.boundingBox);
    box.visible = true;
    post({ type: 'pick', mesh: null, material: info.materialKey, texture: null });
  }
});

window.addEventListener('keydown', (event) => {
  if (event.key === 'f' || event.key === 'F') {
    if (box.visible) look(box.box.clone().expandByScalar(5));
    else if (description.arenas?.length) goToArena(currentArena);
    else frame();
  }
});

function applyTint(keys, tint) {
  currentTint = tint;
  const chosen = new Set(keys.map((key) => key.toLowerCase()));
  for (const item of materials) {
    const on = chosen.has(item.key) || (item.textureKey && chosen.has(item.textureKey)) || (item.glowKey && chosen.has(item.glowKey));
    setTint(item.uniforms, on);
    if (item.sky) setTint(item.skyUniforms, on);
  }
  for (const tile of tiles) {
    const info = tile.userData.tile;
    setTint(info.uniforms, true);
    const whole = chosen.has(info.materialKey);
    info.uniforms.layerTinted.value = info.uniforms.layerTinted.value.map((_, index) => whole || (info.layers[index] && chosen.has(info.layers[index])) ? 1 : 0);
  }
}

host?.addEventListener('message', (event) => {
  const message = event.data;
  if (message.type === 'load') {
    base = message.base;
    load(message.scene, message.base).catch((error) => { status.textContent = 'The map could not be drawn: ' + error.message; post({ type: 'error', message: String(error) }); });
  } else if (message.type === 'select') {
    selected = new Set((message.keys ?? []).map((key) => key.toLowerCase()));
    applySelection();
  } else if (message.type === 'tint') {
    applyTint(message.keys ?? [], message.tint);
  }
});

// For Arkive's own tests: views from fixed directions, a lighting choice and a click.
window.arkiveMap = {
  top() { frame(); camera.position.set(controls.target.x, controls.target.y + camera.position.distanceTo(controls.target), controls.target.z + 0.01); controls.update(); },
  low() { frame(); const offset = camera.position.clone().sub(controls.target); offset.y *= 0.35; camera.position.copy(controls.target).add(offset.multiplyScalar(0.6)); controls.update(); },
  environment(value) { environmentSelect.value = String(value); return applyEnvironment(base); },
  arena(index) { arenaSelect.value = String(index); return goToArena(index); },
  where() { return { target: controls.target.toArray(), camera: camera.position.toArray() }; },
  heights(x, z) {
    const raycaster = new THREE.Raycaster(new THREE.Vector3(x, 5000, z), new THREE.Vector3(0, -1, 0), 0, 10000);
    return raycaster.intersectObjects([...batches.filter((batch) => batch.layer === 'solid').map((batch) => batch.mesh), ...tiles], false)
      .map((hit) => [Math.round(hit.point.y), hit.object.userData.batch?.meshKey ?? 'terrain']);
  },
  game(on, defaults) { gameCheck.checked = on; Object.assign(gameDefaults, defaults ?? {}); return goToArena(currentArena); },
  arenas() { return (description.arenas ?? []).length; },
  visit(index, size) {
    const environment = description.environments[index];
    environmentSelect.value = String(index);
    look(new THREE.Box3().setFromCenterAndSize(new THREE.Vector3(...environment.centre), new THREE.Vector3(size, size, size)));
    return applyEnvironment(base);
  },
  highlighted() { return batches.filter((batch) => batch.on).length; },
  baked(on, strength, gamma) {
    bakedCheck.checked = on; baked.on.value = on ? 1 : 0;
    if (strength !== undefined) baked.strength.value = strength;
    if (gamma !== undefined) baked.gamma.value = gamma;
    return bakedCopies.size;
  },
  pick(x, y) { canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: x, clientY: y, button: 0 })); canvas.dispatchEvent(new PointerEvent('pointerup', { clientX: x, clientY: y, button: 0 })); },
};

function render() {
  requestAnimationFrame(render);
  controls.update();
  renderer.setRenderTarget(target);
  const clipping = renderer.clippingPlanes;
  renderer.clippingPlanes = [];
  renderer.render(skyScene, camera);
  renderer.clippingPlanes = clipping;
  renderer.autoClear = false;
  renderer.clearDepth();
  renderer.render(scene, camera);
  renderer.autoClear = true;
  finish.uniforms.projectionInverse.value.copy(camera.projectionMatrixInverse);
  finish.uniforms.cameraWorld.value.copy(camera.matrixWorld);
  renderer.setRenderTarget(null);
  renderer.render(finishScene, finishCamera);
}
resize();
render();
post({ type: 'ready' });
// Opened outside Arkive (map.html?scene=<the scene folder's address>), to look at a saved map in a browser.
const standalone = !host && new URLSearchParams(location.search).get('scene');
if (standalone) { base = standalone; load(standalone + 'scene.json', standalone).catch((error) => { status.textContent = String(error); }); }
