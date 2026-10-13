import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { VertexNormalsHelper } from 'three/addons/helpers/VertexNormalsHelper.js';
import { VertexTangentsHelper } from 'three/addons/helpers/VertexTangentsHelper.js';

const $ = (id) => document.getElementById(id);
const host = window.chrome?.webview;
const post = (message) => host?.postMessage(message);

const state = {
  scene: null,
  base: '',
  model: null,
  lods: new Map(),
  lod: 0,
  uvSet: 0,
  shading: 'recipe',
  light: 'studio',
  toggles: { wireframe: false, normals: false, tangents: false, skeleton: false, boneNames: false, sockets: false, uvs: false, grid: true, backfaces: false },
  isolatedSection: null,
  selectedMaterial: null,
  helpers: [],
  textures: new Map(),
  materialCache: new Map(),
  bounds: new THREE.Box3(),
};

// Renderer, camera and lights.
const canvas = $('view3d');
const overlay = $('overlay2d');
const overlayContext = overlay.getContext('2d');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(window.devicePixelRatio);
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.NoToneMapping;
const world = new THREE.Scene();
// A photo-studio environment for reflections: dark floor, grey walls, a few softboxes. Metals reflect it,
// so it needs contrast; an evenly bright room makes every metal read as white.
function studioEnvironment() {
  const scene = new THREE.Scene();
  const dome = new THREE.SphereGeometry(10, 48, 24);
  const colours = [];
  const position = dome.attributes.position;
  for (let i = 0; i < position.count; i++) {
    const y = position.getY(i) / 10;
    const shade = y < 0 ? 0.03 + 0.12 * (1 + y) : 0.15 + 0.3 * y;
    colours.push(shade, shade, shade * 1.04);
  }
  dome.setAttribute('color', new THREE.Float32BufferAttribute(colours, 3));
  scene.add(new THREE.Mesh(dome, new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.BackSide })));
  const softbox = (width, height, intensity, x, y, z) => {
    const panel = new THREE.Mesh(new THREE.PlaneGeometry(width, height),
      new THREE.MeshBasicMaterial({ color: new THREE.Color(intensity, intensity, intensity), side: THREE.DoubleSide }));
    panel.position.set(x, y, z);
    panel.lookAt(0, 0, 0);
    scene.add(panel);
  };
  softbox(4, 3, 4, 5, 5, 5);
  softbox(3, 4, 1.6, -6, 2, 3);
  softbox(6, 1.2, 2.5, 0, 6, -5);
  const texture = new THREE.PMREMGenerator(renderer).fromScene(scene, 0.03).texture;
  dome.dispose();
  return texture;
}
const environment = studioEnvironment();
const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 1000);
const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.screenSpacePanning = true;
const lights = {
  hemi: new THREE.HemisphereLight(0xffffff, 0x404048, 1.2),
  key: new THREE.DirectionalLight(0xffffff, 2.2),
  fill: new THREE.DirectionalLight(0xdfe8ff, 0.8),
  rim: new THREE.DirectionalLight(0xffffff, 1.0),
  head: new THREE.DirectionalLight(0xffffff, 2.6),
  ambient: new THREE.AmbientLight(0xffffff, Math.PI),
};
lights.key.position.set(2, 3, 4);
lights.fill.position.set(-4, 1, 2);
lights.rim.position.set(0, 3, -4);
camera.add(lights.head);
world.add(camera, lights.hemi, lights.key, lights.fill, lights.rim, lights.ambient);
const grid = new THREE.GridHelper(4, 40, 0x888888, 0x555555);
grid.material.transparent = true;
grid.material.opacity = 0.35;
world.add(grid);

function applyLighting() {
  const studio = state.light === 'studio';
  lights.hemi.visible = lights.key.visible = lights.fill.visible = lights.rim.visible = studio;
  lights.head.visible = state.light === 'headlight';
  lights.ambient.visible = state.light === 'flat';
  world.environment = state.light === 'flat' ? null : environment;
  if (state.light === 'headlight') lights.hemi.visible = true;
}

function resize() {
  const { clientWidth: w, clientHeight: h } = canvas;
  if (canvas.width !== Math.floor(w * devicePixelRatio) || canvas.height !== Math.floor(h * devicePixelRatio)) {
    renderer.setSize(w, h, false);
    overlay.width = Math.floor(w * devicePixelRatio);
    overlay.height = Math.floor(h * devicePixelRatio);
    camera.aspect = w / Math.max(1, h);
    camera.updateProjectionMatrix();
  }
}

// Textures are sampled as stored; recipes decode sRGB themselves.
function texture(url) {
  if (!url) return null;
  if (!state.textures.has(url)) {
    const t = new THREE.TextureLoader().load(state.base + url, () => requestRender());
    t.colorSpace = THREE.NoColorSpace;
    t.flipY = false;
    t.wrapS = t.wrapT = THREE.RepeatWrapping;
    t.anisotropy = renderer.capabilities.getMaxAnisotropy();
    state.textures.set(url, t);
  }
  return state.textures.get(url);
}

function slotColor(slot) {
  const hue = (slot * 0.61803398875) % 1;
  return new THREE.Color().setHSL(hue, 0.6, 0.55);
}

// Inputs of the game's pixel shader, by semantic. World-space vectors are approximated in view space.
function gameInput(input) {
  const register = `ark_in${input.register}`;
  switch (`${input.semantic}${input.index}`) {
    case 'SV_IsFrontFace0': return `${register} = gl_FrontFacing ? uvec4(0xFFFFFFFFu) : uvec4(0u);`;
    case 'TEXCOORD0': return `${register} = floatBitsToUint(vec4(vUv, ark_vUv1));`;
    case 'TEXCOORD10': return `${register} = floatBitsToUint(vec4(1.0, 0.0, 0.0, 0.0));`;
    case 'TEXCOORD11': return `${register} = floatBitsToUint(vec4(normalize(vNormal), 1.0));`;
    case 'COLOR0': return `${register} = floatBitsToUint(vec4(1.0));`;
    case 'COLOR1': return `${register} = floatBitsToUint(vec4(0.0));`;
    // Height fog: colour in xyz, the share of the surface that survives in w. The viewer has no fog.
    case 'TEXCOORD4': return `${register} = floatBitsToUint(vec4(0.0, 0.0, 0.0, 1.0));`;
    default: return `${register} = floatBitsToUint(vec4(0.0, 0.0, 1.0, 1.0));`;
  }
}

const isTranslucent = (info) => /Translucent|Additive|Modulate/i.test(info?.blendMode ?? '');

/// Standard material whose base colour comes from a colour recipe, or from the game's own
/// translated pixel shader when `game` is given (its roughness and metalness too, when it writes them).
/// Translucent materials are drawn like the game's auto-sorting: an `opaque` part (nearly opaque pixels,
/// writing depth) and a `blend` part (the rest, blended over it), so hair and layered cloth sort correctly.
function recipeMaterial(info, plan, game, bake = false, part = null) {
  const material = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.7, metalness: 0 });
  material.name = info.name;
  material.side = info.twoSided || state.toggles.backfaces ? THREE.DoubleSide : THREE.FrontSide;
  if (isTranslucent(info) && part !== 'opaque') {
    material.transparent = true;
    material.depthWrite = false;
  } else if (info.masked && !game) {
    material.alphaTest = Math.max(0.01, info.opacityMaskClipValue || 0.333);
  }
  if (plan.normalTextureParameter && info.textureUrls[plan.normalTextureParameter]) {
    material.normalMap = texture(info.textureUrls[plan.normalTextureParameter]);
    material.normalScale.set(1, plan.flipNormalGreen ? -1 : 1);
  }
  const uniforms = {};
  for (const [uniform, parameter] of Object.entries(plan.samplers)) uniforms[uniform] = { value: texture(info.textureUrls[parameter]) };
  for (const u of plan.uniforms) uniforms[u.uniform] = { value: u.kind === 'vec4' ? new THREE.Vector4(...u.value) : u.value[0] };
  let declarations = Object.keys(plan.samplers).map((name) => `uniform sampler2D ${name};`)
    .concat(plan.uniforms.map((u) => `uniform ${u.kind} ${u.uniform};`)).join('\n');
  let baseColour = `diffuseColor.rgb *= ${plan.baseColorGlsl};`;
  let opacity = plan.opacityGlsl ? `diffuseColor.a *= ${plan.opacityGlsl};` : null;
  // Auto-sorting shaders draw in two engine passes chosen by a constant they compare with zero; the
  // second pass writes the full opacity to o0.w and discards what the first drew. The viewer runs the
  // second pass with that discard removed and sorts opaque and blended pixels itself.
  const passFlag = game && isTranslucent(info) && /AutoSorting/i.test(game.materialMap)
    ? /equal\(uintBitsToFloat\(floatBitsToUint\(ark_cb0\[(\d+)\]\)\.([xyzw])\2\2\2\), uintBitsToFloat\(uvec4\(0u\)\)\)/.exec(game.function)
    : null;
  if (game) {
    for (const [name, original] of Object.entries(game.constantBuffers)) {
      const values = [...original];
      if (passFlag && name === 'ark_cb0') values[Number(passFlag[1]) * 4 + 'xyzw'.indexOf(passFlag[2])] = 1;
      const vectors = [];
      for (let i = 0; i < values.length; i += 4) vectors.push(new THREE.Vector4(values[i], values[i + 1], values[i + 2], values[i + 3]));
      uniforms[name] = { value: vectors };
      declarations += `\nuniform vec4 ${name}[${vectors.length}];`;
    }
    for (const [name, url] of Object.entries(game.samplers)) {
      uniforms[name] = { value: texture(url) };
      declarations += `\nuniform sampler2D ${name};`;
    }
    declarations += `\n${game.declarations}\n${state.scene.glslPrelude}\n${passFlag ? game.function.replaceAll('discard;', '{}') : game.function}`;
    // Translucent shaders write their opacity to o0.w (negative on back faces of auto-sorted parts).
    const translucent = isTranslucent(info);
    baseColour = `{\n${game.inputs.map(gameInput).join('\n')}\nark_game_shader();\n` +
      `vec4 ark_albedo = uintBitsToFloat(ark_o${game.albedoOutput});\n` +
      // An effect material is all emitted light: nothing of it is lit (a bake keeps its colour).
      `diffuseColor.rgb = ${game.unlit && !bake ? 'vec3(0.0)' : 'clamp(ark_albedo.rgb, 0.0, 16.0)'};\n` +
      (translucent ? 'diffuseColor.a = clamp(abs(uintBitsToFloat(ark_o0).w), 0.0, 1.0);\n' : '') + '}';
    opacity = null;
  }
  // With a separate albedo target, the game's o0 is the base pass's own scene colour: emissive, glow and rim
  // light. Arkive's engine constants (sky and ambient light) are zero, so it adds only what the material emits.
  // Diagnostics: window.arkiveViewer.showOutput('o5.y') shows one game shader output channel as grey.
  if (game && state.debugOutput) {
    const [register, channel] = state.debugOutput.split('.');
    baseColour = baseColour.replace(/\}$/, `diffuseColor.rgb = vec3(0.0);\nark_debug = ${channel === 'nan' ? `(any(isnan(uintBitsToFloat(ark_${register}))) || any(isinf(uintBitsToFloat(ark_${register})))) ? 1.0 : 0.0` : `uintBitsToFloat(ark_${register}).${channel}`};\n}`);
    declarations += '\nfloat ark_debug = 0.0;';
  }
  const gameEmissive = game && state.debugOutput ? 'totalEmissiveRadiance = vec3(ark_debug);' : game?.unlit || game?.emitsSceneColour
    // The viewer has no tone mapping, so bright (HDR) glow is scaled into range keeping its hue.
    ? '{ vec3 ark_glow = max(uintBitsToFloat(ark_o0).rgb, vec3(0.0));' +
      ' totalEmissiveRadiance += ark_glow / max(1.0, max(ark_glow.r, max(ark_glow.g, ark_glow.b))); }' : null;
  const usesSecondUv = plan.usesSecondUv || !!game;
  material.defines = { USE_UV: '' };
  if (usesSecondUv) material.defines.USE_UV1 = '';
  const secondUv = usesSecondUv ? 'varying vec2 ark_vUv1;' : '';
  material.onBeforeCompile = (shader) => {
    Object.assign(shader.uniforms, uniforms);
    if (usesSecondUv) {
      shader.vertexShader = shader.vertexShader
        .replace('#include <common>', `#include <common>\n${secondUv}`)
        .replace('#include <uv_vertex>', '#include <uv_vertex>\nark_vUv1 = uv1;');
    }
    if (bake) {
      // Rasterise in texture space: each triangle lands where its UV set 0 coordinates are.
      shader.vertexShader = shader.vertexShader.replace('#include <project_vertex>',
        '#include <project_vertex>\ngl_Position = vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);');
    }
    let fs = shader.fragmentShader.replace('#include <common>', `#include <common>\n${secondUv}\n${game ? '' : state.scene.glslPrelude}\n${declarations}`);
    fs = fs.replace('#include <map_fragment>', baseColour);
    if (opacity) fs = fs.replace('#include <alphamap_fragment>', opacity);
    if (plan.roughnessGlsl) fs = fs.replace('#include <roughnessmap_fragment>', `float roughnessFactor = clamp(${plan.roughnessGlsl}, 0.04, 1.0);`);
    if (plan.metalnessGlsl) fs = fs.replace('#include <metalnessmap_fragment>', `float metalnessFactor = clamp(${plan.metalnessGlsl}, 0.0, 1.0);`);
    if (game?.writesSurface && !game.unlit && !state.debugOutput) {
      fs = fs.replace('#include <roughnessmap_fragment>', 'float roughnessFactor = clamp(uintBitsToFloat(ark_o2).z, 0.04, 1.0);');
      fs = fs.replace('#include <metalnessmap_fragment>', 'float metalnessFactor = clamp(uintBitsToFloat(ark_o2).w, 0.0, 1.0);');
    }
    if (part === 'opaque') fs = fs.replace('#include <alphatest_fragment>', 'if (diffuseColor.a < 0.98) discard;');
    if (part === 'blend') fs = fs.replace('#include <alphatest_fragment>', 'if (diffuseColor.a >= 0.98 || diffuseColor.a < 0.004) discard;');
    if (gameEmissive) fs = fs.replace('#include <emissivemap_fragment>', gameEmissive);
    else if (plan.emissiveGlsl) fs = fs.replace('#include <emissivemap_fragment>', `totalEmissiveRadiance += ${plan.emissiveGlsl};`);
    if (plan.occlusionGlsl) fs = fs.replace('#include <aomap_fragment>', `reflectedLight.indirectDiffuse *= clamp(${plan.occlusionGlsl}, 0.0, 1.0);`);
    if (game && state.debugOutput) fs = fs.replace('#include <dithering_fragment>', 'gl_FragColor = vec4(vec3(ark_debug), 1.0);');
    if (bake) {
      if (!game) fs = fs.replace('#include <common>', `#include <common>\n${state.scene.glslPrelude}`);
      fs = fs.replace('#include <dithering_fragment>', '#include <dithering_fragment>\ngl_FragColor = vec4(ark_encode_srgb(diffuseColor.rgb), diffuseColor.a);');
    }
    shader.fragmentShader = fs;
  };
  if (bake) {
    material.blending = THREE.NoBlending;
    material.transparent = false;
    material.depthTest = false;
    material.depthWrite = false;
    material.side = THREE.DoubleSide;
  }
  material.customProgramCacheKey = () => [bake, plan.familyId, game?.shaderId, !!gameEmissive, usesSecondUv, plan.baseColorGlsl, plan.opacityGlsl,
    plan.roughnessGlsl, plan.metalnessGlsl, plan.emissiveGlsl, plan.occlusionGlsl, declarations.length, game?.albedoOutput,
    state.debugOutput, part, game?.writesSurface, !!passFlag].join('|');
  return material;
}

function materialFor(slot, part = null) {
  const info = state.scene.materials[slot];
  const key = `${state.shading}:${slot}:${state.toggles.backfaces}:${part}`;
  if (state.materialCache.has(key)) return state.materialCache.get(key);
  let material;
  switch (state.shading) {
    case 'materialIds': material = new THREE.MeshBasicMaterial({ color: slotColor(slot), side: THREE.DoubleSide }); break;
    case 'vertexColors': material = new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide }); break;
    case 'clay': material = new THREE.MeshStandardMaterial({ color: 0xb8b2a8, roughness: 0.8, side: THREE.DoubleSide }); break;
    default:
      if (!info) material = new THREE.MeshStandardMaterial({ color: slotColor(slot) });
      else if (state.shading === 'game' && info.gameShader && !info.gameShaderBroken) {
        material = recipeMaterial(info, info.recipe, info.gameShader, false, part);
        material.userData.gameSlot = slot;
      }
      else material = recipeMaterial(info, state.shading === 'stored' ? info.storedTexture : info.recipe, null, false, part);
  }
  state.materialCache.set(key, material);
  return material;
}

function slotOf(mesh) {
  if (mesh.userData.slot !== undefined) return mesh.userData.slot;
  const original = mesh.userData.originalMaterial;
  const byExtras = original?.userData?.arkiveMaterialSlot;
  let slot = byExtras;
  if (slot === undefined) {
    const name = original?.name?.replace(/\.\d{3}$/, '');
    slot = state.scene.materials.findIndex((m) => m.name === name || m.reference === name || m.name === original?.name);
  }
  mesh.userData.slot = slot;
  return slot;
}

const LAYERED_SHADINGS = new Set(['game', 'recipe', 'stored']);

// The blended part of a translucent mesh: a child mesh sharing its geometry (and skeleton), drawn after it.
function blendCompanion(mesh) {
  if (mesh.userData.companion) return mesh.userData.companion;
  const companion = mesh.isSkinnedMesh ? new THREE.SkinnedMesh(mesh.geometry, mesh.material) : new THREE.Mesh(mesh.geometry, mesh.material);
  if (mesh.isSkinnedMesh) {
    companion.bindMode = mesh.bindMode;
    companion.bind(mesh.skeleton, mesh.bindMatrix);
  }
  companion.frustumCulled = false;
  companion.renderOrder = 1;
  companion.userData.isCompanion = true;
  mesh.add(companion); // inherits the mesh's transform and visibility
  mesh.userData.companion = companion;
  return companion;
}

function applyMaterials() {
  for (const meshes of state.lods.values()) {
    for (const mesh of meshes) {
      const slot = slotOf(mesh);
      const layered = LAYERED_SHADINGS.has(state.shading) && isTranslucent(state.scene.materials[slot]);
      const material = materialFor(slot, layered ? 'opaque' : null);
      material.wireframe = state.toggles.wireframe;
      mesh.material = material;
      const section = mesh.geometry.userData.arkiveSection;
      mesh.visible = state.isolatedSection === null || section === state.isolatedSection;
      if (layered || mesh.userData.companion) {
        const companion = blendCompanion(mesh);
        companion.material = layered ? materialFor(slot, 'blend') : material;
        companion.material.wireframe = state.toggles.wireframe;
        companion.visible = layered && mesh.visible;
      }
    }
  }
  requestRender();
}

function clearHelpers() {
  for (const helper of state.helpers) {
    world.remove(helper);
    helper.geometry?.dispose();
  }
  state.helpers = [];
}

function rebuildHelpers() {
  clearHelpers();
  const meshes = state.lods.get(state.lod) ?? [];
  const size = state.bounds.getSize(new THREE.Vector3()).length() * 0.008;
  if (state.toggles.normals) for (const mesh of meshes) state.helpers.push(new VertexNormalsHelper(mesh, size, 0x33ddff));
  if (state.toggles.tangents) {
    for (const mesh of meshes) if (mesh.geometry.attributes.tangent) state.helpers.push(new VertexTangentsHelper(mesh, size, 0xffaa33));
  }
  if (state.toggles.skeleton && state.model) {
    const helper = new THREE.SkeletonHelper(state.model);
    helper.material.depthTest = false;
    state.helpers.push(helper);
  }
  for (const helper of state.helpers) world.add(helper);
  requestRender();
}

function showLod(index) {
  state.lod = index;
  for (const [lod, meshes] of state.lods) for (const mesh of meshes) mesh.userData.lodNode.visible = lod === index;
  $('lodSelect').value = String(index);
  rebuildHelpers();
  renderInfo();
}

function focus() {
  const meshes = state.lods.get(state.lod) ?? [];
  const box = new THREE.Box3();
  for (const mesh of meshes) box.expandByObject(mesh);
  if (box.isEmpty()) return;
  state.bounds.copy(box);
  const center = box.getCenter(new THREE.Vector3());
  const radius = box.getSize(new THREE.Vector3()).length() / 2;
  const distance = radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2));
  camera.near = Math.max(0.001, radius / 500);
  camera.far = radius * 100;
  camera.updateProjectionMatrix();
  camera.position.copy(center).add(new THREE.Vector3(1, 0.15, 0.35).normalize().multiplyScalar(distance * 1.05));
  controls.target.copy(center);
  controls.update();
  grid.position.set(center.x, box.min.y, center.z);
  grid.scale.setScalar(Math.max(0.25, radius));
  requestRender();
}

async function loadModel() {
  const scene = state.scene;
  if (!scene.modelUrl) return;
  const gltf = await new GLTFLoader().loadAsync(state.base + scene.modelUrl);
  const root = gltf.scene;
  if (scene.geometryUnits === 'unreal') {
    // UModel's glTF is in Unreal centimetres with Z up.
    root.scale.setScalar(0.01);
  }
  root.traverse((object) => {
    if (!object.isMesh) return;
    object.userData.originalMaterial = object.material;
    object.frustumCulled = false;
    let lodNode = object;
    while (lodNode && lodNode.userData.arkiveLod === undefined) lodNode = lodNode.parent;
    let lod = lodNode?.userData.arkiveLod;
    // An edited GLB may have lost Arkive's extras; its mesh names still end in _LOD#.
    for (let node = object; lod === undefined && node; node = node.parent) {
      const match = /_LOD(\d+)/i.exec(node.name ?? '');
      if (match) { lod = Number(match[1]); lodNode = node; }
    }
    lod ??= 0;
    // The node to show or hide for this LOD: a one-section mesh is its own node, so its parent may hold every LOD.
    object.userData.lodNode = lodNode ?? object;
    if (!state.lods.has(lod)) state.lods.set(lod, []);
    state.lods.get(lod).push(object);
  });
  world.add(root);
  state.model = root;
  root.updateMatrixWorld(true);
}

// 2D overlay: bone names, sockets and UV layout.
function drawOverlay() {
  const ctx = overlayContext;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, overlay.width, overlay.height);
  ctx.scale(devicePixelRatio, devicePixelRatio);
  const w = overlay.width / devicePixelRatio;
  const h = overlay.height / devicePixelRatio;
  const bones = new Map();
  if (state.model && (state.toggles.boneNames || state.toggles.sockets)) {
    state.model.traverse((object) => { if (object.isBone) bones.set(object.name.replace(/__ARKIVE_\d+$/, ''), object); });
  }
  const projectPoint = (point) => {
    const p = point.clone().project(camera);
    return p.z > 1 ? null : [(p.x + 1) / 2 * w, (1 - p.y) / 2 * h];
  };
  const project = (object) => projectPoint(object.getWorldPosition(new THREE.Vector3()));
  ctx.font = '11px Consolas, monospace';
  if (state.toggles.boneNames) {
    ctx.fillStyle = '#ffe680';
    for (const [name, bone] of bones) {
      const p = project(bone);
      if (p) ctx.fillText(name, p[0] + 3, p[1] - 3);
    }
  }
  if (state.toggles.sockets) {
    for (const socket of state.scene.sockets) {
      const bone = bones.get(socket.bone);
      if (!bone) continue;
      const p = projectPoint(bone.localToWorld(socketOffset(socket)));
      if (!p) continue;
      ctx.strokeStyle = ctx.fillStyle = '#ff7de0';
      ctx.strokeRect(p[0] - 4, p[1] - 4, 8, 8);
      ctx.fillText(`${socket.name} (${socket.bone})`, p[0] + 7, p[1] + 4);
    }
  }
  if (state.toggles.uvs) drawUvs(ctx, w, h);
}

function drawUvs(ctx, w, h) {
  const size = Math.min(w, h) - 40;
  const x0 = w - size - 20;
  const y0 = 20;
  ctx.fillStyle = 'rgba(0,0,0,0.55)';
  ctx.fillRect(x0, y0, size, size);
  const info = state.selectedMaterial !== null ? state.scene.materials[state.selectedMaterial] : null;
  const backgroundUrl = info && (info.textureUrls[info.recipe.samplers?.ark_t0] ?? Object.values(info.textureUrls)[0]);
  const image = backgroundUrl ? state.textures.get(backgroundUrl)?.image : null;
  if (image?.complete) {
    ctx.globalAlpha = 0.6;
    ctx.drawImage(image, x0, y0, size, size);
    ctx.globalAlpha = 1;
  }
  ctx.strokeStyle = 'rgba(120,200,255,0.55)';
  ctx.lineWidth = 0.5;
  const attribute = state.uvSet === 0 ? 'uv' : `uv${state.uvSet}`;
  for (const mesh of state.lods.get(state.lod) ?? []) {
    if (!mesh.visible || (state.selectedMaterial !== null && slotOf(mesh) !== state.selectedMaterial)) continue;
    const uv = mesh.geometry.attributes[attribute];
    const index = mesh.geometry.index;
    if (!uv || !index) continue;
    ctx.beginPath();
    for (let i = 0; i < index.count; i += 3) {
      for (let k = 0; k <= 3; k++) {
        const v = index.getX(i + (k % 3));
        const px = x0 + (uv.getX(v) % 1 + 1) % 1 * size;
        const py = y0 + (uv.getY(v) % 1 + 1) % 1 * size;
        if (k === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
      }
    }
    ctx.stroke();
  }
  ctx.strokeStyle = '#888';
  ctx.strokeRect(x0, y0, size, size);
}

// Socket offsets are Unreal centimetres in bone space; Arkive's GLB maps Unreal (X,Y,Z) to (X,Z,Y) metres.
function socketOffset(socket) {
  const l = socket.location ?? [0, 0, 0];
  return state.scene.geometryUnits === 'unreal'
    ? new THREE.Vector3(l[0], l[1], l[2])
    : new THREE.Vector3(l[0], l[2], l[1]).multiplyScalar(0.01);
}

function hudText() {
  const scene = state.scene;
  const lod = scene.lods.find((l) => l.index === state.lod);
  const recipes = scene.materials.map((m) => m.recipe.status);
  const lines = [
    `${scene.object.className}  ${scene.object.packageAlias}.${scene.object.objectName}`,
    lod ? `LOD     : ${state.lod + 1}/${scene.lods.length}` : 'LOD     : n/a',
    lod ? `Verts   : ${lod.vertices.toLocaleString()}` : '',
    lod ? `Tris    : ${lod.triangles.toLocaleString()}` : '',
    lod ? `UV Set  : ${state.uvSet + 1}/${lod.uvSets}` : '',
    `Colors  : ${scene.hasVertexColors === null ? 'unknown' : scene.hasVertexColors ? 'present' : 'none'}`,
    scene.bones.length ? `Bones   : ${scene.bones.length}` : '',
    scene.sockets.length ? `Sockets : ${scene.sockets.length}` : '',
    lod ? `Sections: ${lod.sections.length}` : '',
    `Colours : ${$('shadingSelect').selectedOptions[0].text}` +
      (state.shading === 'recipe' ? ` (${recipes.filter((s) => s !== 'fallback').length}/${recipes.length} recipes)` : '') +
      (state.shading === 'game' ? ` (${scene.materials.filter((m) => m.gameShader).length}/${recipes.length} game shaders)` : ''),
  ];
  return lines.filter(Boolean).join('\n');
}

let renderQueued = false;
function requestRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(render);
}

// A translated shader that the browser refuses to compile falls back to the material's colour recipe.
function replaceBrokenGameShaders() {
  let replaced = false;
  for (const [key, material] of state.materialCache) {
    const slot = material.userData.gameSlot;
    if (slot === undefined) continue;
    const program = renderer.properties.get(material).currentProgram;
    if (!program?.diagnostics || program.diagnostics.runnable) continue;
    const info = state.scene.materials[slot];
    info.gameShaderBroken = true;
    info.gameShaderFailure = `The browser could not compile this shader: ${program.diagnostics.fragmentShader.log || program.diagnostics.programLog}`.slice(0, 400);
    state.materialCache.delete(key);
    replaced = true;
  }
  if (replaced) {
    applyMaterials();
    renderMaterials();
  }
}

function render() {
  renderQueued = false;
  resize();
  grid.visible = state.toggles.grid;
  renderer.render(world, camera);
  if (state.shading === 'game') replaceBrokenGameShaders();
  if (state.scene) {
    drawOverlay();
    $('hud').textContent = hudText();
  }
}
controls.addEventListener('change', requestRender);
(function loop() { if (controls.update()) requestRender(); requestAnimationFrame(loop); })();
new ResizeObserver(requestRender).observe(canvas);

// Side panel.
const escapeHtml = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const kv = (rows) => `<table class="kv">${rows.filter((r) => r[1] !== undefined && r[1] !== null && r[1] !== '')
  .map(([k, v]) => `<tr><td>${escapeHtml(k)}</td><td>${v}</td></tr>`).join('')}</table>`;
const badge = (status) => `<span class="badge ${status}" title="${{ verified: 'Checked against the game', inferred: 'Not yet checked against the game', fallback: 'No recipe: shown as stored, like UModel' }[status]}">${status}</span>`;
const linearToCss = (v) => {
  const c = (x) => Math.round(255 * Math.min(1, Math.max(0, x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055)));
  return `rgb(${c(v[0])},${c(v[1])},${c(v[2])})`;
};

function renderInfo() {
  const scene = state.scene;
  const lod = scene.lods.find((l) => l.index === state.lod);
  let html = `<h3>Object</h3>${kv([
    ['Package', `<span class="mono">${escapeHtml(scene.object.packageAlias)}</span>`],
    ['File', `<span class="mono">${escapeHtml(scene.object.packageFile)}</span>`],
    ['Class', escapeHtml(scene.object.className)],
    ['Object', `<span class="mono">${escapeHtml(scene.object.objectName)}</span>`],
    ['Export', `#${scene.object.exportIndex}`],
    ['Geometry', { 'arkive-native': 'Arkive native decoder (all LODs)', 'umodel-gltf': 'UModel glTF (LOD 0)', edited: 'Your edited mesh' }[scene.geometrySource] ?? 'none'],
  ])}`;
  if (scene.edits) {
    html += `<h3>Your edits · ${escapeHtml(scene.edits.label)}</h3>`;
    html += scene.edits.applied.map((line) => `<div class="row">${escapeHtml(line)}</div>`).join('') ||
      '<div class="row muted">None of the edits belong to this object.</div>';
    html += scene.edits.problems.map((line) => `<div class="row warning">${escapeHtml(line)}</div>`).join('');
    html += '<div class="row muted">Saving a working file refreshes this view.</div>';
  }
  html += `<h3>LODs</h3>`;
  for (const l of scene.lods) {
    const hasGeometry = state.lods.has(l.index);
    html += `<div class="row ${l.index === state.lod ? 'selected' : ''}" ${hasGeometry ? `data-lod="${l.index}"` : 'title="Only LOD 0 geometry is available for this object"'}>LOD ${l.index}
      <span class="muted">${l.vertices.toLocaleString()} verts · ${l.triangles.toLocaleString()} tris · ${l.sections.length} sections · ${l.uvSets} UV${hasGeometry ? '' : ' · statistics only'}</span></div>`;
  }
  if (lod) {
    html += `<h3>Sections in LOD ${lod.index}</h3>`;
    for (const s of lod.sections) {
      const m = scene.materials[s.materialSlot];
      html += `<div class="row ${state.isolatedSection === s.index ? 'selected' : ''}" data-section="${s.index}" title="Click to isolate this section">
        <span class="swatch" style="background:${slotColor(s.materialSlot).getStyle()}"></span>
        ${s.index}: ${escapeHtml(m?.name ?? s.material ?? 'unresolved')}<span class="muted">${s.triangles.toLocaleString()} tris</span></div>`;
    }
  }
  html += `<h3>Skeleton</h3>${kv([['Bones', scene.bones.length], ['Sockets', scene.sockets.length],
    ['Vertex colours', scene.hasVertexColors === null ? 'unknown' : scene.hasVertexColors ? 'present' : 'none']])}`;
  if (scene.sockets.length) {
    html += `<h3>Sockets</h3>` + scene.sockets.map((s) => `<div class="mono" title="${s.location ? `offset ${s.location.join(', ')} cm; rotation ${s.rotation?.join(', ')}` : ''}">${escapeHtml(s.name)} → ${escapeHtml(s.bone)}</div>`).join('');
  }
  if (scene.diagnostics.length) html += `<h3>Notes</h3>${scene.diagnostics.map((d) => `<div class="note">${escapeHtml(d)}</div>`).join('')}`;
  html += `<h3>Prepared</h3><div class="muted">${scene.preparationSeconds}s · ${new Date(scene.createdUtc).toLocaleString()}</div>`;
  $('tab-info').innerHTML = html;
}

function renderMaterials() {
  const scene = state.scene;
  $('tab-materials').innerHTML = scene.materials.map((m) => {
    const plan = m.recipe;
    const vectors = Object.entries(m.vectors).map(([k, v]) => [k, `<span class="swatch" style="display:inline-block;vertical-align:middle;background:${linearToCss(v)}"></span> <span class="mono">${v.map((x) => +x.toFixed(4)).join(', ')}</span>`]);
    const scalars = Object.entries(m.scalars).map(([k, v]) => [k, `<span class="mono">${+v.toFixed(4)}</span>`]);
    const textures = Object.entries(m.textures).map(([k, v]) => [k, m.textureUrls[k]
      ? `<a href="#" data-texture="${escapeHtml(m.textureUrls[k])}" data-name="${escapeHtml(v)}" class="mono">${escapeHtml(v)}</a>`
      : `<span class="mono muted">${escapeHtml(v)} (not exported)</span>`]);
    return `<details ${state.selectedMaterial === m.slot ? 'open' : ''} data-material="${m.slot}">
      <summary><span class="swatch" style="background:${slotColor(m.slot).getStyle()}"></span>${m.slot}: ${escapeHtml(m.name)} ${badge(plan.status)}</summary>
      <div>
        ${kv([
          ['Class', escapeHtml(m.className)],
          ['Parents', m.parentChain.map(escapeHtml).join(' → ') || '—'],
          ['Blend', `${escapeHtml(m.blendMode)}${m.masked ? ` (clip ${m.opacityMaskClipValue})` : ''}${m.twoSided ? ', two-sided' : ''}`],
          ['Recipe', `${escapeHtml(plan.familyLabel)} ${plan.matchedMaster ? `<span class="muted">via ${escapeHtml(plan.matchedMaster)}</span>` : ''}`],
        ])}
        ${m.gameShader && !m.gameShaderBroken ? `<div>Game shader: <span class="mono">${escapeHtml(m.gameShader.materialMap)}</span> <span class="badge verified">${m.gameShader.exactVariant ? 'exact variant' : 'nearest variant'}</span> <span class="muted">${m.gameShader.instructionCount} instructions</span></div>${m.gameShader.notes.map((n) => `<div class="note">${escapeHtml(n)}</div>`).join('')}`
          : m.gameShaderFailure ? `<div class="note">Game shader unavailable: ${escapeHtml(m.gameShaderFailure)}</div>` : ''}
        ${plan.evidence ? `<div class="muted">${escapeHtml(plan.evidence)}</div>` : ''}
        ${plan.warnings.length ? plan.warnings.map((w) => `<div class="note">${escapeHtml(w)}</div>`).join('') : ''}
        ${textures.length ? `<h3>Texture parameters</h3>${kv(textures)}` : ''}
        ${vectors.length ? `<h3>Vector parameters</h3>${kv(vectors)}` : ''}
        ${scalars.length ? `<h3>Scalar parameters</h3>${kv(scalars)}` : ''}
      </div></details>`;
  }).join('');
}

function renderTextures() {
  $('tab-textures').innerHTML = `<div class="thumbs">${state.scene.textures.map((t) => `
    <div class="thumb" data-texture="${escapeHtml(t.url)}" data-name="${escapeHtml(t.name)}" title="${escapeHtml(t.name)}">
      <img loading="lazy" src="${escapeHtml(state.base + t.url)}" alt="">
      <span>${escapeHtml(t.name)}</span><span class="muted">${t.width}×${t.height} ${escapeHtml(t.pixelFormat)}</span>
    </div>`).join('')}</div>`;
}

// Texture pane with channel isolation, zoom and pan.
const texView = { image: null, channel: 'rgb', zoom: 1, panX: 0, panY: 0, name: '' };
function openTexture(url, name) {
  const image = new Image();
  image.onload = () => {
    texView.image = image; texView.zoom = 0; texView.panX = 0; texView.panY = 0; texView.name = name;
    const info = state.scene.textures.find((t) => t.url === url);
    $('textureTitle').textContent = `${name}  ${info ? `${info.width}×${info.height} ${info.pixelFormat}` : ''}`;
    $('texturePane').hidden = false;
    drawTexture();
  };
  image.src = state.base + url;
}

function drawTexture() {
  const view = $('textureCanvas');
  const image = texView.image;
  if (!image) return;
  view.width = view.clientWidth * devicePixelRatio;
  view.height = view.clientHeight * devicePixelRatio;
  const ctx = view.getContext('2d');
  ctx.fillStyle = '#555';
  ctx.fillRect(0, 0, view.width, view.height);
  const fit = Math.min(view.width / image.width, view.height / image.height) * 0.95;
  const scale = texView.zoom || fit;
  const w = image.width * scale;
  const h = image.height * scale;
  const x = (view.width - w) / 2 + texView.panX;
  const y = (view.height - h) / 2 + texView.panY;
  const source = document.createElement('canvas');
  source.width = image.width;
  source.height = image.height;
  const sctx = source.getContext('2d', { willReadFrequently: true });
  sctx.drawImage(image, 0, 0);
  if (texView.channel !== 'rgba') {
    const data = sctx.getImageData(0, 0, image.width, image.height);
    const p = data.data;
    const pick = { r: 0, g: 1, b: 2, a: 3 }[texView.channel];
    for (let i = 0; i < p.length; i += 4) {
      if (pick !== undefined) { const v = p[i + pick]; p[i] = p[i + 1] = p[i + 2] = v; }
      p[i + 3] = 255;
    }
    sctx.putImageData(data, 0, 0);
  } else {
    ctx.fillStyle = ctx.createPattern(checker(), 'repeat');
    ctx.fillRect(x, y, w, h);
  }
  ctx.imageSmoothingEnabled = scale < 1;
  ctx.drawImage(source, x, y, w, h);
}

function checker() {
  const c = document.createElement('canvas');
  c.width = c.height = 16;
  const x = c.getContext('2d');
  x.fillStyle = '#777'; x.fillRect(0, 0, 16, 16);
  x.fillStyle = '#999'; x.fillRect(0, 0, 8, 8); x.fillRect(8, 8, 8, 8);
  return c;
}

// Events.
document.querySelectorAll('.tabs button').forEach((button) => button.addEventListener('click', () => {
  document.querySelectorAll('.tabs button').forEach((b) => b.classList.toggle('active', b === button));
  document.querySelectorAll('.tab').forEach((t) => t.classList.toggle('active', t.id === `tab-${button.dataset.tab}`));
}));

document.querySelectorAll('[data-toggle]').forEach((button) => button.addEventListener('click', () => toggle(button.dataset.toggle)));

function toggle(name, value) {
  state.toggles[name] = value ?? !state.toggles[name];
  document.querySelector(`[data-toggle="${name}"]`)?.setAttribute('aria-pressed', String(state.toggles[name]));
  if (name === 'wireframe' || name === 'backfaces') applyMaterials();
  if (['normals', 'tangents', 'skeleton'].includes(name)) rebuildHelpers();
  requestRender();
}

$('lodSelect').addEventListener('change', (e) => showLod(Number(e.target.value)));
$('editsToggle').addEventListener('click', () => post({ type: 'showEdits', value: $('editsToggle').getAttribute('aria-pressed') !== 'true' }));
$('uvSelect').addEventListener('change', (e) => { state.uvSet = Number(e.target.value); requestRender(); });
$('shadingSelect').addEventListener('change', (e) => { state.shading = e.target.value; applyMaterials(); });
$('lightSelect').addEventListener('change', (e) => { state.light = e.target.value; applyLighting(); requestRender(); });
$('focusButton').addEventListener('click', focus);
$('screenshotButton').addEventListener('click', (e) => screenshot(e.altKey));
$('umodelButton').addEventListener('click', () => post({ type: 'openUmodel' }));
$('bakeButton').addEventListener('click', () => post({ type: 'requestBake' }));
$('textureClose').addEventListener('click', () => { $('texturePane').hidden = true; });
document.querySelectorAll('[data-channel]').forEach((button) => button.addEventListener('click', () => {
  texView.channel = button.dataset.channel;
  document.querySelectorAll('[data-channel]').forEach((b) => b.classList.toggle('active', b === button));
  drawTexture();
}));
$('textureCanvas').addEventListener('wheel', (e) => {
  e.preventDefault();
  const view = $('textureCanvas');
  const fit = Math.min(view.width / texView.image.width, view.height / texView.image.height) * 0.95;
  texView.zoom = Math.min(32, Math.max(0.02, (texView.zoom || fit) * (e.deltaY < 0 ? 1.2 : 1 / 1.2)));
  drawTexture();
}, { passive: false });
$('textureCanvas').addEventListener('pointermove', (e) => {
  if (e.buttons !== 1) return;
  texView.panX += e.movementX * devicePixelRatio;
  texView.panY += e.movementY * devicePixelRatio;
  drawTexture();
});

$('panel').addEventListener('click', (e) => {
  const target = e.target.closest('[data-lod],[data-section],[data-texture],details[data-material] > summary');
  if (!target) return;
  if (target.dataset.lod !== undefined) showLod(Number(target.dataset.lod));
  else if (target.dataset.section !== undefined) {
    const section = Number(target.dataset.section);
    state.isolatedSection = state.isolatedSection === section ? null : section;
    applyMaterials();
    renderInfo();
  } else if (target.dataset.texture) {
    e.preventDefault();
    openTexture(target.dataset.texture, target.dataset.name);
  } else {
    const slot = Number(target.parentElement.dataset.material);
    state.selectedMaterial = state.selectedMaterial === slot ? null : slot;
    requestRender();
  }
});

$('propertySearch').addEventListener('input', renderProperties);
let propertyLines = [];
function renderProperties() {
  const filter = $('propertySearch').value.trim().toLowerCase();
  $('propertyText').textContent = filter ? propertyLines.filter((l) => l.toLowerCase().includes(filter)).join('\n') : propertyLines.join('\n');
}

function cycle(select, step = 1) {
  const count = select.options.length;
  for (let i = 1; i <= count; i++) {
    const index = (select.selectedIndex + step * i + count * count) % count;
    if (select.options[index].disabled) continue;
    select.selectedIndex = index;
    select.dispatchEvent(new Event('change'));
    return;
  }
}

window.addEventListener('keydown', (e) => {
  if (e.target instanceof HTMLInputElement) return;
  const key = e.key.toLowerCase();
  if (key === 'escape') { $('texturePane').hidden = true; return; }
  if (e.ctrlKey && key === 's') { e.preventDefault(); screenshot(false); return; }
  if (e.altKey && key === 's') { e.preventDefault(); screenshot(true); return; }
  if (e.ctrlKey && key === 'b') { e.preventDefault(); toggle('boneNames'); return; }
  if (e.ctrlKey && key === 'u') { e.preventDefault(); cycle($('uvSelect')); return; }
  if (e.ctrlKey && key === 'l') { e.preventDefault(); cycle($('lightSelect')); return; }
  if (key === 'pageup' || key === 'pagedown') { e.preventDefault(); post({ type: 'browse', delta: key === 'pageup' ? -1 : 1 }); return; }
  if (e.ctrlKey || e.altKey || e.metaKey) return;
  const actions = {
    l: () => cycle($('lodSelect')), w: () => toggle('wireframe'), n: () => toggle('normals'), t: () => toggle('tangents'),
    b: () => toggle('skeleton'), s: () => toggle('sockets'), u: () => toggle('uvs'), g: () => toggle('grid'), f: focus,
    c: () => cycle($('shadingSelect')),
    e: () => { if (!$('editsToggle').hidden) $('editsToggle').click(); },
    m: () => { $('shadingSelect').value = state.shading === 'materialIds' ? 'recipe' : 'materialIds'; $('shadingSelect').dispatchEvent(new Event('change')); },
    v: () => { $('shadingSelect').value = state.shading === 'vertexColors' ? 'recipe' : 'vertexColors'; $('shadingSelect').dispatchEvent(new Event('change')); },
  };
  if (actions[key]) { e.preventDefault(); actions[key](); }
});

function screenshot(transparent) {
  const previous = grid.visible;
  if (transparent) { grid.visible = false; renderer.setClearColor(0x000000, 0); }
  renderer.render(world, camera);
  const data = canvas.toDataURL('image/png');
  grid.visible = previous;
  requestRender();
  post({ type: 'screenshot', dataUrl: data, name: `${state.scene.object.objectName}_lod${state.lod}${transparent ? '_alpha' : ''}` });
}

// Loading.
async function load(message) {
  // A refresh of the same object (its files changed) keeps the camera and view settings.
  const keep = message.keepView && state.scene && state.model ? {
    position: camera.position.clone(), target: controls.target.clone(), lod: state.lod, uvSet: state.uvSet,
    shading: state.shading, isolatedSection: state.isolatedSection,
  } : null;
  $('loading').hidden = false;
  $('loading').textContent = `Loading ${message.scene.object.objectName}…`;
  if (message.theme) document.documentElement.dataset.theme = message.theme;
  for (const t of state.textures.values()) t.dispose();
  for (const m of state.materialCache.values()) m.dispose();
  if (state.model) world.remove(state.model);
  clearHelpers();
  Object.assign(state, { scene: message.scene, base: message.baseUrl, model: null, lod: 0, uvSet: 0, isolatedSection: null, selectedMaterial: null });
  state.lods = new Map();
  state.textures = new Map();
  state.materialCache = new Map();
  const scene = state.scene;
  const hasGame = scene.materials.some((m) => m.gameShader);
  $('shadingSelect').querySelector('option[value="game"]').disabled = !hasGame;
  state.shading = keep && (keep.shading !== 'game' || hasGame) ? keep.shading : hasGame ? 'game' : 'recipe';
  $('shadingSelect').value = state.shading;
  $('editsToggle').hidden = !message.hasEdits;
  $('editsToggle').setAttribute('aria-pressed', String(!!scene.edits));
  $('lodSelect').innerHTML = scene.lods.map((l) => `<option value="${l.index}">${l.index}</option>`).join('');
  const uvSets = Math.max(1, ...scene.lods.map((l) => l.uvSets));
  $('uvSelect').innerHTML = Array.from({ length: uvSets }, (_, i) => `<option value="${i}">${i}</option>`).join('');
  try {
    await loadModel();
    for (const option of $('lodSelect').options) option.disabled = !state.lods.has(Number(option.value));
    applyMaterials();
    applyLighting();
    if (keep && state.lods.has(keep.lod)) {
      state.uvSet = Math.min(keep.uvSet, Math.max(0, $('uvSelect').options.length - 1));
      $('uvSelect').value = String(state.uvSet);
      showLod(keep.lod);
      camera.position.copy(keep.position);
      controls.target.copy(keep.target);
      controls.update();
    } else {
      showLod(scene.lods[0]?.index ?? 0);
      focus();
    }
  } catch (error) {
    $('statusbar').textContent = `Could not load the model: ${error.message}`;
  }
  renderMaterials();
  renderTextures();
  try {
    propertyLines = (await (await fetch(state.base + scene.propertiesUrl)).text()).split(/\r?\n/);
  } catch { propertyLines = ['UModel properties are unavailable.']; }
  renderProperties();
  const counts = scene.materials.reduce((a, m) => (a[m.recipe.status] = (a[m.recipe.status] ?? 0) + 1, a), {});
  const games = scene.materials.filter((m) => m.gameShader).length;
  $('statusbar').textContent = (scene.edits ? `With your edits (${scene.edits.applied.length}) · ` : '') +
    `${scene.object.packageAlias}.${scene.object.objectName} · ${scene.materials.length} materials ` +
    `(${games} using the game's shader; recipes: ${counts.verified ?? 0} verified, ${counts.inferred ?? 0} inferred, ${counts.fallback ?? 0} stored-texture) · ` +
    `${scene.textures.length} textures · prepared in ${scene.preparationSeconds}s`;
  $('loading').hidden = true;
  requestRender();
  post({ type: 'loaded' });
}

host?.addEventListener('message', (event) => {
  const message = event.data;
  if (message?.type === 'load') load(message);
  if (message?.type === 'theme') document.documentElement.dataset.theme = message.theme;
  if (message?.type === 'progress') { $('loading').hidden = false; $('loading').textContent = message.text; }
  if (message?.type === 'error') { $('loading').hidden = false; $('loading').textContent = message.text; }
});

// Outside Arkive (for development): viewer.html?scene=<url of scene.json>
const sceneUrl = new URLSearchParams(location.search).get('scene');
if (sceneUrl) {
  fetch(sceneUrl).then((r) => r.json()).then((scene) => load({ scene, baseUrl: sceneUrl.replace(/scene\.json$/, '') }));
}
for (const name of Object.keys(state.toggles)) document.querySelector(`[data-toggle="${name}"]`)?.setAttribute('aria-pressed', String(state.toggles[name]));
post({ type: 'ready' });

// Baking: renders each material's colour in texture space (UV set 0) into PNGs for exports.
const coverageMaterial = new THREE.ShaderMaterial({
  vertexShader: '#include <common>\n#include <skinning_pars_vertex>\nvoid main() { gl_Position = vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0); }',
  fragmentShader: 'void main() { gl_FragColor = vec4(1.0); }',
  side: THREE.DoubleSide,
  depthTest: false,
  depthWrite: false,
});

function bakeSize(info) {
  const plan = state.shading === 'game' && info.gameShader && !info.gameShaderBroken ? info.gameShader : null;
  const urls = plan ? Object.values(plan.samplers) : Object.values(info.textureUrls);
  const sizes = urls.map((url) => state.scene.textures.find((t) => t.url === url)?.width ?? 0);
  const size = Math.max(256, ...sizes);
  return Math.min(2048, 2 ** Math.round(Math.log2(size)));
}

function renderBake(meshes, material, size) {
  const target = new THREE.WebGLRenderTarget(size, size, { depthBuffer: false });
  const bakeScene = new THREE.Scene();
  const proxies = meshes.map((mesh) => {
    const proxy = mesh.isSkinnedMesh ? new THREE.SkinnedMesh(mesh.geometry, material) : new THREE.Mesh(mesh.geometry, material);
    if (mesh.isSkinnedMesh) proxy.bind(mesh.skeleton, mesh.bindMatrix);
    proxy.frustumCulled = false;
    bakeScene.add(proxy);
    return proxy;
  });
  const previousTarget = renderer.getRenderTarget();
  const previousClear = renderer.getClearColor(new THREE.Color());
  const previousAlpha = renderer.getClearAlpha();
  renderer.setRenderTarget(target);
  renderer.setClearColor(0x000000, 0);
  renderer.clear();
  renderer.render(bakeScene, camera);
  const pixels = new Uint8Array(size * size * 4);
  renderer.readRenderTargetPixels(target, 0, 0, size, size, pixels);
  renderer.setRenderTarget(previousTarget);
  renderer.setClearColor(previousClear, previousAlpha);
  target.dispose();
  for (const proxy of proxies) bakeScene.remove(proxy);
  return pixels;
}

// Pads UV islands outwards so mip-mapping and filtering never sample the empty background.
function dilate(pixels, coverage, size, passes) {
  let covered = new Uint8Array(size * size);
  for (let i = 0; i < covered.length; i++) covered[i] = coverage[i * 4] > 0 ? 1 : 0;
  for (let pass = 0; pass < passes; pass++) {
    const next = covered.slice();
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const i = y * size + x;
        if (covered[i]) continue;
        let r = 0, g = 0, b = 0, n = 0;
        for (let dy = -1; dy <= 1; dy++) {
          const yy = y + dy;
          if (yy < 0 || yy >= size) continue;
          for (let dx = -1; dx <= 1; dx++) {
            const xx = x + dx;
            if (xx < 0 || xx >= size || !covered[yy * size + xx]) continue;
            const j = (yy * size + xx) * 4;
            r += pixels[j]; g += pixels[j + 1]; b += pixels[j + 2]; n++;
          }
        }
        if (!n) continue;
        const k = i * 4;
        pixels[k] = r / n; pixels[k + 1] = g / n; pixels[k + 2] = b / n;
        next[i] = 1;
      }
    }
    covered = next;
  }
}

function encodePng(pixels, size) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const context = canvas.getContext('2d');
  const image = context.createImageData(size, size);
  // Render-target rows run bottom-up; images run top-down.
  for (let y = 0; y < size; y++) image.data.set(pixels.subarray((size - 1 - y) * size * 4, (size - y) * size * 4), y * size * 4);
  context.putImageData(image, 0, 0);
  return canvas.toDataURL('image/png');
}

async function bakeTextures(requestId) {
  const lod = Math.min(...state.lods.keys());
  const bySlot = new Map();
  for (const mesh of state.lods.get(lod) ?? []) {
    const slot = slotOf(mesh);
    if (!bySlot.has(slot)) bySlot.set(slot, []);
    bySlot.get(slot).push(mesh);
  }
  let count = 0;
  for (const [slot, meshes] of bySlot) {
    const info = state.scene.materials[slot];
    if (!info || !meshes.some((mesh) => mesh.geometry.attributes.uv)) continue;
    const game = state.shading === 'game' && info.gameShader && !info.gameShaderBroken ? info.gameShader : null;
    const material = recipeMaterial(info, info.recipe, game, true);
    const size = bakeSize(info);
    const colour = renderBake(meshes, material, size);
    const coverage = renderBake(meshes, coverageMaterial, size);
    material.dispose();
    dilate(colour, coverage, size, 8);
    post({ type: 'baked', requestId, slot, material: info.reference ?? info.name, name: info.name, source: game ? 'game shader' : info.recipe.familyId,
      size, dataUrl: encodePng(colour, size) });
    count++;
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  post({ type: 'bakeDone', requestId, count });
}

host?.addEventListener('message', (event) => {
  if (event.data?.type === 'bake') bakeTextures(event.data.requestId).catch((error) => post({ type: 'bakeDone', requestId: event.data.requestId, count: 0, error: String(error) }));
});

// Diagnostics handle for Arkive's host and tests.
window.arkiveViewer = {
  state,
  renderer,
  camera,
  controls,
  // Automated check: renders each material alone from the front and reports its pixel statistics
  // (share of near-white and near-black pixels, mean colour) and shader compile errors.
  materialStats: () => {
    focus();
    const gl = renderer.getContext();
    const width = gl.drawingBufferWidth, height = gl.drawingBufferHeight;
    const pixels = new Uint8Array(width * height * 4);
    const previousClear = renderer.getClearColor(new THREE.Color());
    const previousAlpha = renderer.getClearAlpha();
    const gridVisible = grid.visible;
    grid.visible = false;
    renderer.setClearColor(0x000000, 0);
    const meshes = [...(state.lods.get(state.lod) ?? [])];
    const results = [];
    for (let slot = 0; slot < state.scene.materials.length; slot++) {
      const own = meshes.filter((mesh) => slotOf(mesh) === slot);
      if (own.length === 0) continue;
      for (const mesh of meshes) mesh.visible = own.includes(mesh);
      const box = new THREE.Box3();
      const point = new THREE.Vector3();
      for (const mesh of own) {
        const position = mesh.geometry.attributes.position;
        for (let i = 0; i < position.count; i += 7) box.expandByPoint(point.fromBufferAttribute(position, i).applyMatrix4(mesh.matrixWorld));
      }
      const saved = [camera.position.clone(), controls.target.clone()];
      const radius = Math.max(1e-3, box.getSize(new THREE.Vector3()).length() / 2);
      controls.target.copy(box.getCenter(new THREE.Vector3()));
      // The same viewing direction as Focus.
      camera.position.copy(controls.target).add(new THREE.Vector3(1, 0.15, 0.35).normalize()
        .multiplyScalar(radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2))));
      camera.lookAt(controls.target);
      camera.updateMatrixWorld();
      renderer.clear();
      renderer.render(world, camera);
      gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
      camera.position.copy(saved[0]);
      controls.target.copy(saved[1]);
      controls.update();
      let covered = 0, white = 0, black = 0, r = 0, g = 0, b = 0;
      for (let i = 0; i < pixels.length; i += 16) {
        const alpha = pixels[i + 3];
        if (alpha < 128) continue;
        covered++;
        // The canvas holds premultiplied colour.
        const pr = pixels[i] * 255 / alpha, pg = pixels[i + 1] * 255 / alpha, pb = pixels[i + 2] * 255 / alpha;
        r += pr; g += pg; b += pb;
        if (pr > 240 && pg > 240 && pb > 240) white++;
        if (pr < 8 && pg < 8 && pb < 8) black++;
      }
      const info = state.scene.materials[slot];
      results.push({
        slot, name: info.name, master: info.rootMaterial, blend: info.blendMode,
        game: !!(info.gameShader && !info.gameShaderBroken), failure: info.gameShaderFailure ?? null,
        covered, white: covered ? +(white / covered).toFixed(3) : 0, black: covered ? +(black / covered).toFixed(3) : 0,
        mean: covered ? [r / covered, g / covered, b / covered].map((v) => Math.round(v)) : null,
      });
    }
    for (const mesh of meshes) mesh.visible = true;
    grid.visible = gridVisible;
    renderer.setClearColor(previousClear, previousAlpha);

    applyMaterials();
    return { errors: (window.arkiveViewer.programErrors?.() ?? []).length, materials: results };
  },
  showOutput: (output) => {
    state.debugOutput = output || null;
    for (const m of state.materialCache.values()) m.dispose();
    state.materialCache = new Map();
    applyMaterials();
    requestRender();
    return 'ok';
  },
  // Orbits the camera around the model (degrees) at a distance factor, for checks from several sides.
  orbit: (yaw, pitch = 10, distance = 1) => {
    focus();
    const offset = camera.position.clone().sub(controls.target);
    const spherical = new THREE.Spherical().setFromVector3(offset);
    spherical.theta = THREE.MathUtils.degToRad(yaw);
    spherical.phi = THREE.MathUtils.degToRad(90 - pitch);
    spherical.radius *= distance;
    camera.position.copy(controls.target).add(new THREE.Vector3().setFromSpherical(spherical));
    controls.update();
    requestRender();
  },
  // Frames a bone (by name fragment) at the given radius, for close-ups of hands, heads and so on.
  frameBone: (name, radius = 0.25, yaw = 0) => {
    focus();
    let bone = null;
    state.model.traverse((o) => { if (!bone && o.isBone && o.name.toLowerCase().includes(name.toLowerCase())) bone = o; });
    if (!bone) return 'no bone';
    const direction = camera.position.clone().sub(controls.target).normalize()
      .applyAxisAngle(new THREE.Vector3(0, 1, 0), THREE.MathUtils.degToRad(yaw));
    bone.getWorldPosition(controls.target);
    camera.position.copy(controls.target).addScaledVector(direction, radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2)));
    controls.update();
    requestRender();
    return bone.name;
  },
  // Frames the given material slot's meshes from the default viewing direction, for close-ups.
  frameSlot: (slot, distance = 1, yaw = 0) => {
    focus();
    const box = new THREE.Box3();
    // Sections share one vertex buffer, so bound the vertices each section's indices use.
    const point = new THREE.Vector3();
    for (const mesh of state.lods.get(state.lod) ?? []) {
      if (slotOf(mesh) !== slot) continue;
      const position = mesh.geometry.attributes.position;
      const index = mesh.geometry.index;
      for (let i = 0, n = index ? index.count : position.count; i < n; i++) {
        point.fromBufferAttribute(position, index ? index.getX(i) : i).applyMatrix4(mesh.matrixWorld);
        box.expandByPoint(point);
      }
    }
    if (box.isEmpty()) return 'empty';
    const direction = camera.position.clone().sub(controls.target).normalize()
      .applyAxisAngle(new THREE.Vector3(0, 1, 0), THREE.MathUtils.degToRad(yaw));
    const radius = box.getSize(new THREE.Vector3()).length() / 2;
    controls.target.copy(box.getCenter(new THREE.Vector3()));
    camera.position.copy(controls.target).addScaledVector(direction, distance * radius / Math.sin(THREE.MathUtils.degToRad(camera.fov / 2)));
    controls.update();
    requestRender();
    return 'ok';
  },
  bake: () => bakeTextures('debug'),
  bakeStats: (slot, size = 256) => {
    const info = state.scene.materials[slot];
    const meshes = [...state.lods.get(Math.min(...state.lods.keys()))].filter((mesh) => slotOf(mesh) === slot);
    const material = recipeMaterial(info, info.recipe, info.gameShader, true);
    const pixels = renderBake(meshes, material, size);
    let lit = 0, alpha = 0, sum = 0;
    for (let i = 0; i < pixels.length; i += 4) { if (pixels[i] + pixels[i + 1] + pixels[i + 2]) lit++; if (pixels[i + 3]) alpha++; sum += pixels[i]; }
    const program = renderer.properties.get(material).currentProgram;
    return { meshes: meshes.length, lit, alpha, mean: sum / (size * size), runnable: program?.diagnostics?.runnable ?? 'n/a',
      log: program?.diagnostics?.fragmentShader?.log?.slice(0, 300) };
  },
  coverage: (size = 256) => {
    const pixels = renderBake([...state.lods.get(Math.min(...state.lods.keys()))], coverageMaterial, size);
    let covered = 0;
    for (let i = 0; i < pixels.length; i += 4) if (pixels[i]) covered++;
    return { covered, error: renderer.getContext().getError(), programs: renderer.info.programs.length };
  },
  programErrors: () => renderer.info.programs
    .filter((program) => program.diagnostics && !program.diagnostics.runnable)
    .map((program) => `${program.name}: ${program.diagnostics.fragmentShader.log || program.diagnostics.programLog}`.slice(0, 600)),
};
