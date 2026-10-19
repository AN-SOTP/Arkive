// Arkive particle effect preview: plays one particle system with Arkive's shared simulation (particle-sim.js) and
// applies the edit recipe live as the controls move.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { ParticleEffect, EffectAssets, defaultRecipe } from './particle-sim.js';
import { createEffectsPass } from './effects-pass.js';

const host = window.chrome?.webview;
const post = (message) => host?.postMessage(message);
const $ = (id) => document.getElementById(id);

const renderer = new THREE.WebGLRenderer({ canvas: $('canvas'), antialias: true });
const effectsPass = createEffectsPass(renderer);
renderer.setPixelRatio(window.devicePixelRatio);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(45, 1, 1, 200000);
camera.position.set(500, 350, 700);
const controls = new OrbitControls(camera, renderer.domElement);
controls.target.set(0, 80, 0);
controls.update();
const grid = new THREE.GridHelper(2000, 40, 0x555a62, 0x3a3f47);
scene.add(grid);
const backgrounds = { dark: 0x22252a, black: 0x000000, grey: 0x5a5f66, light: 0xd8dce2 };
scene.background = new THREE.Color(backgrounds.dark);

const SWATCHES = [
  ['Red', 1, 0.04, 0.02], ['Orange', 1, 0.35, 0.02], ['Gold', 1, 0.75, 0.1], ['Green', 0.1, 1, 0.15], ['Cyan', 0.05, 0.85, 1],
  ['Blue', 0.1, 0.3, 1], ['Purple', 0.6, 0.1, 1], ['Pink', 1, 0.2, 0.65], ['White', 1, 1, 1],
];
const MULTIPLIERS = [
  ['brightness', 'Brightness'], ['opacity', 'Opacity'], ['size', 'Size'], ['lifetime', 'Lifetime'], ['spawnRate', 'Spawn rate'], ['speed', 'Speed'],
];

const assets = new EffectAssets();
// Exposed for Arkive's tests.
const state = window.arkiveParticles = {
  model: null, effect: null, playing: true, speed: 1, loop: true, last: 0, frames: 0, recipe: defaultRecipe(), assets,
  get emitters() { return this.effect?.emitters ?? []; },
  get time() { return this.effect?.time ?? 0; },
};
state.view = { renderer, camera, controls };

// ---- Playback ----
function restart() { state.effect?.reset(); }

function tick(now) {
  const dt = state.last ? Math.min(0.05, (now - state.last) / 1000) : 0;
  state.last = now;
  const effect = state.effect;
  if (effect && state.playing) {
    effect.update(dt * state.speed);
    if (state.loop && effect.time > effect.finishedAt && !effect.alive) restart();
  }
  effect?.write();
  if (effect && ++state.frames % 30 === 1) assets.checkPrograms(renderer, [effect]);
  $('timeText').textContent = effect ? `${effect.time.toFixed(2)} s · ${effect.particleCount} particles` : '';
  resize();
  renderer.render(scene, camera);
  effectsPass.render(scene, camera);
  requestAnimationFrame(tick);
}

function resize() {
  const canvas = renderer.domElement;
  const width = canvas.clientWidth, height = canvas.clientHeight;
  if (canvas.width !== Math.floor(width * renderer.getPixelRatio()) || canvas.height !== Math.floor(height * renderer.getPixelRatio())) {
    renderer.setSize(width, height, false);
    camera.aspect = width / Math.max(1, height);
    camera.updateProjectionMatrix();
  }
}

function focus() {
  // Frame where most particles are: a few fast ones flying off would otherwise pull the camera far away.
  const points = state.emitters.flatMap((emitter) => emitter.visible ? emitter.particles.map((p) => p.pos) : []);
  let center = new THREE.Vector3(0, 100, 0), radius = 250;
  if (points.length) {
    center = points.reduce((sum, p) => sum.add(p), new THREE.Vector3()).divideScalar(points.length);
    const distances = points.map((p) => p.distanceTo(center)).sort((a, b) => a - b);
    radius = Math.min(3000, Math.max(80, distances[Math.floor(distances.length * 0.85)] ?? 250));
  }
  const direction = camera.position.clone().sub(controls.target).normalize();
  controls.target.copy(center);
  camera.position.copy(center).addScaledVector(direction, radius * 2.6);
  controls.update();
}

// ---- Controls ----
function readRecipe() {
  const tinted = $('tintCheck').checked;
  const recipe = {
    tint: tinted ? { red: +$('tintRed').value, green: +$('tintGreen').value, blue: +$('tintBlue').value } : null,
    tintStrength: +$('tintStrength').value,
    tintSelection: $('tintSelection').value,
    tintHue: +$('tintHue').value,
    tintRange: +$('tintRange').value,
  };
  for (const [key] of MULTIPLIERS) recipe[key] = +$(`m-${key}`).value;
  return recipe;
}

function showRecipe(recipe) {
  $('tintCheck').checked = !!recipe.tint;
  const tint = recipe.tint ?? { red: 1, green: 1, blue: 1 };
  $('tintRed').value = tint.red; $('tintGreen').value = tint.green; $('tintBlue').value = tint.blue;
  $('tintStrength').value = recipe.tintStrength ?? 1;
  $('tintSelection').value = recipe.tintSelection ?? 'All';
  $('tintHue').value = recipe.tintHue ?? 45;
  $('tintRange').value = recipe.tintRange ?? 25;
  for (const [key] of MULTIPLIERS) $(`m-${key}`).value = recipe[key] ?? 1;
  refreshOutputs();
}

function refreshOutputs() {
  for (const id of ['tintRed', 'tintGreen', 'tintBlue']) $(`${id}Text`).textContent = (+$(id).value).toFixed(2);
  $('tintStrengthText').textContent = `${Math.round(+$('tintStrength').value * 100)}%`;
  for (const [key] of MULTIPLIERS) $(`m-${key}Text`).textContent = `${(+$(`m-${key}`).value).toFixed(2)}×`;
  const [r, g, b] = ['tintRed', 'tintGreen', 'tintBlue'].map((id) => Math.round(Math.pow(+$(id).value, 1 / 2.2) * 255));
  $('tintPreview').style.background = $('tintCheck').checked ? `rgb(${r},${g},${b})` : 'transparent';
  $('tintRangePanel').hidden = $('tintSelection').value !== 'ColourRange';
  $('tintHueText').textContent = `${$('tintHue').value}°`;
  $('tintRangeText').textContent = `±${$('tintRange').value}°`;
  const [hue, width] = [+$('tintHue').value, +$('tintRange').value];
  const stops = [0, 1, 2, 3, 4, 5, 6].map((step) => `hsl(${(hue - width + width * step / 3 + 360) % 360}, 100%, 50%)`);
  $('tintRangeBand').style.background = `linear-gradient(to right, ${stops.join(', ')})`;
}

function recipeChanged() {
  refreshOutputs();
  state.recipe = readRecipe();
  state.effect?.applyRecipe(state.recipe);
  post({ type: 'recipe', recipe: state.recipe });
}

function buildControls() {
  for (const [name, r, g, b] of SWATCHES) {
    const button = document.createElement('button');
    button.title = name;
    button.style.background = `rgb(${[r, g, b].map((v) => Math.round(Math.pow(v, 1 / 2.2) * 255)).join(',')})`;
    button.addEventListener('click', () => {
      $('tintCheck').checked = true;
      $('tintRed').value = r; $('tintGreen').value = g; $('tintBlue').value = b;
      recipeChanged();
    });
    $('swatches').appendChild(button);
  }
  for (const [key, label] of MULTIPLIERS) {
    const row = document.createElement('div');
    row.className = 'slider';
    row.innerHTML = `<span>${label}</span><input type="range" id="m-${key}" min="0" max="4" step="0.05" value="1" /><output id="m-${key}Text"></output>`;
    $('multipliers').appendChild(row);
  }
  for (const input of document.querySelectorAll('#panel input[type="range"], #tintCheck, #tintSelection')) input.addEventListener('input', recipeChanged);
  $('resetButton').addEventListener('click', () => { showRecipe(defaultRecipe()); recipeChanged(); });
  $('playButton').addEventListener('click', () => { state.playing = !state.playing; $('playButton').textContent = state.playing ? 'Pause' : 'Play'; });
  $('restartButton').addEventListener('click', restart);
  $('speedSelect').addEventListener('change', (e) => { state.speed = +e.target.value; });
  $('loopCheck').addEventListener('change', (e) => { state.loop = e.target.checked; });
  $('gridCheck').addEventListener('change', (e) => { grid.visible = e.target.checked; });
  $('backgroundSelect').addEventListener('change', (e) => { scene.background = new THREE.Color(backgrounds[e.target.value]); });
  $('focusButton').addEventListener('click', focus);
  window.addEventListener('keydown', (e) => {
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
    if (e.code === 'Space') { e.preventDefault(); $('playButton').click(); }
    else if (e.key === 'r' || e.key === 'R') restart();
    else if (e.key === 'f' || e.key === 'F') focus();
  });
}

function buildEmitterList() {
  const list = $('emitterList');
  list.innerHTML = '';
  $('emitterCount').textContent = `(${state.emitters.length})`;
  for (const emitter of state.emitters) {
    const label = document.createElement('label');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.checked = emitter.visible;
    box.addEventListener('change', () => { emitter.visible = box.checked; });
    label.append(box, document.createTextNode(emitter.model.name));
    const detail = document.createElement('span');
    detail.className = 'detail';
    const kind = emitter.isLight ? 'light (not drawn)' : emitter.isDistortion ? 'distortion (not drawn)' : emitter.isMesh ? 'mesh' : emitter.isDecal ? 'decal' : null;
    detail.textContent = [kind, emitter.model.enabled ? null : 'off in game', emitter.materialName].filter(Boolean).join(' · ');
    label.title = detail.textContent;
    label.append(detail);
    list.appendChild(label);
  }
}

function load(message) {
  if (message.theme) document.documentElement.dataset.theme = message.theme;
  if (message.theme === 'light') { scene.background = new THREE.Color(backgrounds.light); $('backgroundSelect').value = 'light'; }
  state.effect?.dispose();
  state.model = message.model;
  state.recipe = { ...defaultRecipe(), ...(message.recipe ?? {}) };
  showRecipe(state.recipe);
  state.effect = new ParticleEffect({ model: message.model, root: scene, assets, recipe: state.recipe });
  buildEmitterList();
  restart();
  $('loading').style.display = 'none';
  $('status').textContent = `${message.model.system} · ${message.model.emitters.length} emitter(s) · ${message.model.packageAlias}`;
  setTimeout(focus, 900);
  post({ type: 'loaded' });
}

host?.addEventListener('message', (event) => {
  const message = event.data;
  if (message?.type === 'load') load(message);
  else if (message?.type === 'textures') assets.add({ textures: message.items, meshes: message.meshes, prelude: message.prelude });
  else if (message?.type === 'status') $('status').textContent = message.text;
  else if (message?.type === 'error') { $('loading').style.display = ''; $('loading').textContent = message.text; }
});

buildControls();
refreshOutputs();
requestAnimationFrame(tick);
post({ type: 'ready' });
