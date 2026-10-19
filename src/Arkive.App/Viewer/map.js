// Arkive's 3D map: a whole map from the Zone Viewer's scene (scene.json + scene.bin + small textures), drawn as
// instanced meshes and terrain tiles. Clicking a piece tells the host which mesh and material it is; the host sends
// back which parts are selected (highlighted) and the recolour being set up (previewed on their textures).
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const host = window.chrome?.webview;
const post = (message) => host?.postMessage(message);
const canvas = document.getElementById('view');
const status = document.getElementById('status');

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, logarithmicDepthBuffer: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x2a3140);
const camera = new THREE.PerspectiveCamera(55, 1, 0.5, 200000);
const controls = new OrbitControls(camera, canvas);
controls.enableDamping = true;
controls.screenSpacePanning = true;
scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x3b342c, 1.6));
const sun = new THREE.DirectionalLight(0xffffff, 1.6);
sun.position.set(0.4, 1, 0.3);
scene.add(sun);

const highlight = new THREE.Color(1.0, 0.62, 0.25);
// Sky domes and cloud planes wrap the whole map, and glows, water and light shafts drawn solid cover it, so both
// layers start hidden.
const layerChecks = { sky: document.getElementById('skyCheck'), effect: document.getElementById('effectCheck') };
const layerVisible = (layer) => layerChecks[layer]?.checked ?? true;
for (const check of Object.values(layerChecks))
  check.addEventListener('change', () => { for (const batch of batches) batch.mesh.visible = layerVisible(batch.layer); });
const white = new THREE.Color(1, 1, 1);
let materials = [];        // { key, textureKey, material, image (ImageData), colour }
let batches = [];          // { mesh: InstancedMesh, meshKey, slotKeys[] }
let tiles = [];            // { mesh, materialKey }
let selected = new Set();  // part keys the host has selected
let picked = null;         // { batch, instance } last clicked
const box = new THREE.Box3Helper(new THREE.Box3(), 0xffa040);
box.visible = false;
scene.add(box);

function resize() {
  const width = canvas.clientWidth, height = canvas.clientHeight;
  renderer.setSize(width, height, false);
  camera.aspect = width / Math.max(1, height);
  camera.updateProjectionMatrix();
}
window.addEventListener('resize', resize);

async function load(sceneUrl, baseUrl) {
  status.textContent = 'Loading the map…';
  const description = await (await fetch(sceneUrl)).json();
  const data = await (await fetch(baseUrl + 'scene.bin')).arrayBuffer();
  const loader = new THREE.TextureLoader();
  materials = await Promise.all(description.materials.map(async (item) => {
    const material = new THREE.MeshLambertMaterial({ side: THREE.DoubleSide, color: new THREE.Color().setRGB(...item.colour, THREE.SRGBColorSpace) });
    const entry = { key: item.key.toLowerCase(), textureKey: item.textureKey?.toLowerCase() ?? null, material, image: null, texture: null, colour: material.color.clone() };
    if (item.texture) {
      try {
        const texture = await loader.loadAsync(baseUrl + item.texture);
        texture.colorSpace = THREE.SRGBColorSpace;
        texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
        texture.magFilter = THREE.LinearFilter;
        entry.image = pixels(texture.image);
        entry.texture = new THREE.DataTexture(new Uint8ClampedArray(entry.image.data), entry.image.width, entry.image.height);
        entry.texture.colorSpace = THREE.SRGBColorSpace;
        entry.texture.wrapS = entry.texture.wrapT = THREE.RepeatWrapping;
        entry.texture.magFilter = THREE.LinearFilter;
        entry.texture.minFilter = THREE.LinearMipmapLinearFilter;
        entry.texture.generateMipmaps = true;
        entry.texture.flipY = false;
        entry.texture.needsUpdate = true;
        material.map = entry.texture;
        material.color.set(0xffffff);
        entry.colour = material.color.clone();
      } catch { /* a missing texture leaves the colour */ }
    }
    return entry;
  }));
  const fallback = new THREE.MeshLambertMaterial({ color: 0x8c8c8c, side: THREE.DoubleSide });
  const geometries = description.meshes.map((mesh) => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(data, mesh.positions.offset, mesh.positions.count * 3), 3));
    geometry.setAttribute('uv', new THREE.Float16BufferAttribute(new Uint16Array(data, mesh.uvs.offset, mesh.uvs.count * 2), 2));
    geometry.setIndex(new THREE.BufferAttribute(new Uint16Array(data, mesh.indices.offset, mesh.indices.count), 1));
    for (const section of mesh.sections) geometry.addGroup(section.firstIndex, section.indexCount, section.material);
    geometry.computeVertexNormals();
    geometry.computeBoundingSphere();
    return geometry;
  });
  const matrix = new THREE.Matrix4();
  for (const batch of description.batches) {
    const geometry = geometries[batch.mesh];
    const slots = batch.materials.map((index) => index >= 0 ? materials[index].material : fallback);
    const mesh = new THREE.InstancedMesh(geometry, slots.length ? slots : fallback, batch.matrices.count);
    const values = new Float32Array(data, batch.matrices.offset, batch.matrices.count * 16);
    for (let index = 0; index < batch.matrices.count; index++) {
      matrix.fromArray(values, index * 16);
      mesh.setMatrixAt(index, matrix);
      mesh.setColorAt(index, white);
    }
    mesh.instanceMatrix.needsUpdate = true;
    mesh.computeBoundingSphere();
    const meshKey = description.meshes[batch.mesh].key.toLowerCase();
    const entry = { mesh, meshKey, layer: batch.layer ?? 'solid', slotKeys: batch.materials.map((index) => index >= 0 ? materials[index].key : null) };
    mesh.visible = layerVisible(entry.layer);
    mesh.userData.batch = entry;
    batches.push(entry);
    scene.add(mesh);
  }
  for (const tile of description.terrain) {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(data, tile.positions.offset, tile.positions.count * 3), 3));
    geometry.setAttribute('uv', new THREE.Float16BufferAttribute(new Uint16Array(data, tile.uvs.offset, tile.uvs.count * 2), 2));
    geometry.setIndex(new THREE.BufferAttribute(new Uint16Array(data, tile.indices.offset, tile.indices.count), 1));
    geometry.computeVertexNormals();
    const info = { materialKey: tile.material >= 0 ? materials[tile.material].key : null, layers: null, weights: null };
    let material = tile.material >= 0 ? materials[tile.material].material : fallback;
    if (tile.layers?.length && tile.weights) {
      // The ground's own blend: each vertex mixes its layers' colours by the tile's blend masks.
      info.layers = tile.layers.map((layer) => ({ texture: layer.texture?.toLowerCase() ?? null, colour: new THREE.Color().setRGB(...layer.colour, THREE.SRGBColorSpace) }));
      info.weights = new Uint8Array(data, tile.weights.offset, tile.weights.count);
      geometry.setAttribute('color', new THREE.BufferAttribute(new Float32Array(tile.positions.count * 3), 3));
      material = new THREE.MeshLambertMaterial({ vertexColors: true, side: THREE.DoubleSide });
      colourTile(geometry, info, null, new Set());
    }
    const mesh = new THREE.Mesh(geometry, material);
    mesh.userData.tile = info;
    tiles.push(mesh);
    scene.add(mesh);
  }
  frame();
  status.textContent = '';
  post({ type: 'loaded', placements: description.placements, meshes: description.meshes.length, terrain: description.terrain.length });
  applySelection();
}

// The play area: the terrain when the map has one, otherwise where most pieces stand (cinematic sets and skies sit
// far away from it).
function frame() {
  if (tiles.length) {
    const bounds = new THREE.Box3();
    for (const tile of tiles) { tile.geometry.computeBoundingBox(); bounds.union(tile.geometry.boundingBox); }
    look(bounds);
    return;
  }
  const cell = 150, counts = new Map(), points = [];
  const position = new THREE.Vector3(), matrix = new THREE.Matrix4();
  for (const batch of batches) {
    if (batch.layer !== 'solid') continue;
    for (let index = 0; index < batch.mesh.count; index++) {
      batch.mesh.getMatrixAt(index, matrix);
      position.setFromMatrixPosition(matrix);
      points.push(position.clone());
      const key = `${Math.floor(position.x / cell)},${Math.floor(position.z / cell)}`;
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  }
  if (!points.length) return;
  const [best] = [...counts.entries()].sort((a, b) => b[1] - a[1])[0];
  const [cx, cz] = best.split(',').map(Number);
  const near = points.filter((point) => Math.abs(point.x / cell - cx - 0.5) < 3 && Math.abs(point.z / cell - cz - 0.5) < 3);
  look(new THREE.Box3().setFromPoints(near));
}

function look(bounds) {
  const centre = bounds.getCenter(new THREE.Vector3());
  const size = Math.max(10, bounds.getSize(new THREE.Vector3()).length());
  controls.target.copy(centre);
  camera.position.copy(centre).add(new THREE.Vector3(0.55, 0.6, 0.75).normalize().multiplyScalar(size * 0.9));
  camera.near = Math.max(0.1, size / 5000);
  camera.far = size * 40;
  camera.updateProjectionMatrix();
  controls.update();
}

// A tile's vertex colours from its layers, with the layers the recolour applies to tinted.
function colourTile(geometry, info, tint, chosen) {
  const colours = geometry.getAttribute('color');
  const count = info.layers.length;
  const layerColours = info.layers.map((layer) => {
    if (!tint || !(chosen.has(info.materialKey) || (layer.texture && chosen.has(layer.texture)))) return layer.colour;
    const srgb = layer.colour.clone().convertLinearToSRGB();
    const pixel = [srgb.r * 255, srgb.g * 255, srgb.b * 255, 255], out = [0, 0, 0, 255];
    recolour(pixel, out, tint);
    return new THREE.Color().setRGB(out[0] / 255, out[1] / 255, out[2] / 255, THREE.SRGBColorSpace);
  });
  for (let vertex = 0; vertex < colours.count; vertex++) {
    let r = 0, g = 0, b = 0, total = 0;
    for (let layer = 0; layer < count; layer++) {
      const weight = info.weights[vertex * count + layer];
      r += layerColours[layer].r * weight; g += layerColours[layer].g * weight; b += layerColours[layer].b * weight; total += weight;
    }
    if (total === 0) { r = layerColours[0].r; g = layerColours[0].g; b = layerColours[0].b; total = 1; }
    colours.setXYZ(vertex, r / total, g / total, b / total);
  }
  colours.needsUpdate = true;
}

function pixels(image) {
  const surface = document.createElement('canvas');
  surface.width = image.width; surface.height = image.height;
  const context = surface.getContext('2d', { willReadFrequently: true });
  context.drawImage(image, 0, 0);
  return context.getImageData(0, 0, image.width, image.height);
}

// ---- Selection ----

function matches(key) { return key && selected.has(key); }

function applySelection() {
  for (const batch of batches) {
    const on = matches(batch.meshKey) || batch.slotKeys.some((key) => matches(key) || matches(materials.find((item) => item.key === key)?.textureKey));
    batch.on = on;
    for (let index = 0; index < batch.mesh.count; index++) batch.mesh.setColorAt(index, on ? highlight : white);
    batch.mesh.instanceColor.needsUpdate = true;
  }
  for (const tile of tiles) {
    const info = tile.userData.tile;
    const on = matches(info.materialKey) || matches(materials.find((item) => item.key === info.materialKey)?.textureKey) ||
      (info.layers ?? []).some((layer) => matches(layer.texture));
    tile.material.emissive?.set(on ? 0x2a1a08 : 0x000000);
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
  const hit = raycaster.intersectObjects([...batches.filter((batch) => batch.mesh.visible).map((batch) => batch.mesh), ...tiles], false)[0];
  if (!hit) { box.visible = false; picked = null; return; }
  if (hit.object.userData.batch) {
    const batch = hit.object.userData.batch;
    const slot = hit.face ? (hit.object.geometry.groups.find((group) => hit.faceIndex * 3 >= group.start && hit.faceIndex * 3 < group.start + group.count)?.materialIndex ?? 0) : 0;
    picked = { batch, instance: hit.instanceId };
    const matrix = new THREE.Matrix4();
    batch.mesh.getMatrixAt(hit.instanceId, matrix);
    hit.object.geometry.computeBoundingBox();
    box.box.copy(hit.object.geometry.boundingBox).applyMatrix4(matrix);
    box.visible = true;
    const material = batch.slotKeys[slot];
    post({ type: 'pick', mesh: batch.meshKey, material, texture: materials.find((item) => item.key === material)?.textureKey ?? null });
  } else {
    const key = hit.object.userData.tile.materialKey;
    hit.object.geometry.computeBoundingBox();
    box.box.copy(hit.object.geometry.boundingBox);
    box.visible = true;
    post({ type: 'pick', mesh: null, material: key, texture: materials.find((item) => item.key === key)?.textureKey ?? null });
  }
});

window.addEventListener('keydown', (event) => {
  if (event.key === 'f' || event.key === 'F') {
    if (box.visible) look(box.box.clone().expandByScalar(5));
    else frame();
  }
});

// ---- Recolour preview (the Texture Viewer's rule: TextureRecolour and ColourSelections in Arkive.Core) ----

function hue(red, green, blue) {
  const max = Math.max(red, green, blue), delta = max - Math.min(red, green, blue);
  if (delta <= 0) return 0;
  const value = max === red ? ((green - blue) / delta) % 6 : max === green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
  return (value * 60 + 360) % 360;
}

const ramp = (value, from, to) => Math.min(1, Math.max(0, (value - from) / (to - from)));

function weight(tint, red, green, blue, lightness) {
  if (tint.selection === 'All') return 1;
  const strongest = Math.max(red, green, blue), weakest = Math.min(red, green, blue);
  if (strongest <= 0 || weakest < 0) return 0;
  const saturation = (strongest - weakest) / strongest;
  if (tint.selection === 'LightAreas') return ramp(0.25 - saturation, 0, 0.12) * ramp(lightness, 0.5, 0.75);
  const distance = Math.abs((((hue(red, green, blue) - tint.rangeHue) % 360) + 540) % 360 - 180);
  return ramp(tint.rangeWidth + 12 - distance, 0, 12) * ramp(saturation, 0.08, 0.2);
}

function recolour(source, target, tint) {
  const peak = Math.max(tint.red, tint.green, tint.blue) || 1;
  const [r, g, b] = [tint.red / peak, tint.green / peak, tint.blue / peak];
  const scale = 1 + tint.brightness;
  const mix = (original, tinted, amount) => Math.round(original + ((original + (tinted - original) * tint.strength) * scale - original) * amount);
  for (let index = 0; index < source.length; index += 4) {
    const red = source[index], green = source[index + 1], blue = source[index + 2];
    const amount = weight(tint, red, green, blue, Math.max(red, green, blue) / 255);
    if (amount <= 0) { target[index] = red; target[index + 1] = green; target[index + 2] = blue; continue; }
    const strongest = Math.max(red, green, blue);
    target[index] = mix(red, strongest * r, amount);
    target[index + 1] = mix(green, strongest * g, amount);
    target[index + 2] = mix(blue, strongest * b, amount);
  }
}

function applyTint(keys, tint) {
  const chosen = new Set(keys.map((key) => key.toLowerCase()));
  for (const tile of tiles) if (tile.userData.tile.layers) colourTile(tile.geometry, tile.userData.tile, tint, chosen);
  for (const item of materials) {
    const on = tint && (chosen.has(item.key) || (item.textureKey && chosen.has(item.textureKey)));
    if (item.texture && item.image) {
      const target = item.texture.image.data;
      if (on) recolour(item.image.data, target, tint); else target.set(item.image.data);
      item.texture.needsUpdate = true;
    } else {
      const base = item.colour;
      if (on) {
        const pixel = [base.r * 255, base.g * 255, base.b * 255, 255];
        const out = [0, 0, 0, 255];
        recolour(pixel, out, tint);
        item.material.color.setRGB(out[0] / 255, out[1] / 255, out[2] / 255);
      } else item.material.color.copy(base);
    }
  }
}

host?.addEventListener('message', (event) => {
  const message = event.data;
  if (message.type === 'load') {
    document.documentElement.dataset.theme = message.theme === 'Light' ? 'light' : 'dark';
    load(message.scene, message.base).catch((error) => { status.textContent = 'The map could not be drawn: ' + error.message; post({ type: 'error', message: String(error) }); });
  } else if (message.type === 'select') {
    selected = new Set((message.keys ?? []).map((key) => key.toLowerCase()));
    applySelection();
  } else if (message.type === 'tint') {
    applyTint(message.keys ?? [], message.tint);
  }
});

// For Arkive's own tests: views from fixed directions.
window.arkiveMap = {
  top() { frame(); camera.position.set(controls.target.x, controls.target.y + camera.position.distanceTo(controls.target), controls.target.z + 0.01); controls.update(); },
  highlighted() { return batches.filter((batch) => batch.on).length; },
  pick(x, y) { canvas.dispatchEvent(new PointerEvent('pointerdown', { clientX: x, clientY: y, button: 0 })); canvas.dispatchEvent(new PointerEvent('pointerup', { clientX: x, clientY: y, button: 0 })); },
};

function render() {
  requestAnimationFrame(render);
  controls.update();
  renderer.render(scene, camera);
}
resize();
render();
post({ type: 'ready' });
