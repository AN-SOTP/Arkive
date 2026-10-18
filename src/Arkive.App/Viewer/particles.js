// Arkive particle effect preview: plays a particle system's emitters from the settings Arkive read out of the game
// package (the baked value tables the game itself samples), and applies the edit recipe live as the controls move.
// It approximates the game's renderer: sprites and mesh emitters are drawn as camera-facing particles, the effect's
// materials as their first texture with the material's blend mode.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const host = window.chrome?.webview;
const post = (message) => host?.postMessage(message);
const $ = (id) => document.getElementById(id);

const renderer = new THREE.WebGLRenderer({ canvas: $('canvas'), antialias: true });
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
const MAX_PARTICLES = 4000;

// Exposed for Arkive's tests.
const state = window.arkiveParticles = { model: null, emitters: [], playing: true, speed: 1, loop: true, time: 0, last: 0, textures: {}, recipe: defaultRecipe() };
state.view = { renderer, camera, controls };

function defaultRecipe() {
  return { tint: null, tintStrength: 1, brightness: 1, opacity: 1, size: 1, lifetime: 1, spawnRate: 1, speed: 1 };
}

// ---- Edit recipe, the same rules as Arkive's ParticleEffects.Apply ----
function applyRecipe(table, property, recipe) {
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
function sample(table, time, random) {
  const dims = table.dimensions, chunk = table.chunkSize, v = table.values;
  const entries = Math.max(1, Math.floor((v.length - 2) / chunk));
  const position = Math.max(0, (time - table.startTime) * table.timeScale);
  const i0 = Math.min(entries - 1, Math.floor(position));
  const i1 = Math.min(entries - 1, i0 + 1);
  const t = Math.min(1, position - i0);
  const out = new Array(dims);
  for (let d = 0; d < dims; d++) {
    const at = (i, offset) => v[2 + i * chunk + offset + d];
    let a = at(i0, 0), b = at(i1, 0);
    if (chunk >= dims * 2) {
      const r = random[d % random.length];
      a += (at(i0, dims) - a) * r;
      b += (at(i1, dims) - b) * r;
    }
    out[d] = a + (b - a) * t;
  }
  return dims === 1 ? out[0] : out;
}

// Unreal is Z-up; the preview is Y-up.
const toThree = (v) => new THREE.Vector3(v[0], v[2], v[1]);

class Emitter {
  constructor(model, index) {
    this.model = model;
    this.index = index;
    this.visible = model.enabled;
    const required = this.module('required');
    const value = (module, name, fallback) => module?.values?.[name] ?? fallback;
    this.duration = Math.max(0.05, value(required, 'emitterduration', 1));
    this.loops = value(required, 'emitterloops', 0);
    this.delay = value(required, 'emitterdelay', 0);
    this.materialName = model.material;
    this.isMesh = !!model.mesh;
    this.particles = [];
    this.random = Math.random;
    this.buildMaterial();
    this.applyRecipe(state.recipe);
    this.reset();
  }

  module(fragment) { return this.model.modules.find((module) => module.class.includes(fragment)); }
  modules(fragment) { return this.model.modules.filter((module) => module.class.includes(fragment)); }

  applyRecipe(recipe) {
    // Each setting is the first table of that name among the modules; over-life multipliers stack.
    const tables = {};
    const lists = { lifemultiplier: [], colorscaleoverlife: [], alphascaleoverlife: [] };
    for (const module of this.model.modules) {
      for (const [name, value] of Object.entries(module.values)) {
        if (!value || !Array.isArray(value.values)) continue;
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

  reset() {
    this.particles.length = 0;
    this.emitTime = 0;
    this.spawnCarry = 0;
    this.loopIndex = 0;
    this.burstsFired = new Set();
    this.done = false;
  }

  get finishedAt() { return this.loops === 0 ? Infinity : this.delay + this.duration * this.loops; }

  spawnRate(time) {
    const rate = this.tables.rate ?? this.tables.spawnrate;
    let value = rate ? sample(rate, time, [0.5]) : (this.bursts.length ? 0 : 20 * state.recipe.spawnRate);
    if (this.tables.ratescale) value *= sample(this.tables.ratescale, time / this.duration, [0.5]);
    return Math.max(0, value);
  }

  spawn() {
    if (this.particles.length >= MAX_PARTICLES) return;
    const rnd = () => [this.random(), this.random(), this.random()];
    const t = this.tables;
    const life = t.lifetime ? Math.max(0.01, sample(t.lifetime, this.emitTime, rnd())) : this.duration;
    // Without a size module the game sizes the sprite from where it is placed (a skill scales its effect); a
    // medium size keeps such emitters visible in the preview.
    const size = t.startsize ? sample(t.startsize, this.emitTime, rnd()) : [40, 40, 40];
    const scale = this.isMesh ? 60 : 1;
    const p = {
      age: 0, life,
      size: [Math.abs(size[0]) * scale, Math.abs(size[1]) * scale],
      pos: t.startlocation ? toThree(sample(t.startlocation, this.emitTime, rnd())) : new THREE.Vector3(),
      vel: t.startvelocity ? toThree(sample(t.startvelocity, this.emitTime, rnd())) : new THREE.Vector3(),
      color: t.startcolor ? sample(t.startcolor, this.emitTime, rnd()) : [1, 1, 1],
      alpha: t.startalpha ? sample(t.startalpha, this.emitTime, rnd()) : 1,
      rot: t.startrotation ? sample(t.startrotation, this.emitTime, rnd()) * Math.PI * 2 : this.random() * Math.PI * 2,
      rotRate: t.startrotationrate ? sample(t.startrotationrate, this.emitTime, rnd()) * Math.PI * 2 : 0,
      seeds: rnd(),
    };
    const sphere = this.module('locationprimitivesphere');
    const cylinder = this.module('locationprimitivecylinder') ?? this.module('circlesurface');
    if (sphere || cylinder) {
      const radius = t.startradius ? Math.abs(sample(t.startradius, this.emitTime, rnd())) : 50;
      const direction = new THREE.Vector3();
      if (sphere) direction.randomDirection();
      else { const a = this.random() * Math.PI * 2; direction.set(Math.cos(a), 0, Math.sin(a)); }
      const surface = (sphere ?? cylinder).values.surfaceonly ?? false;
      const offset = direction.clone().multiplyScalar(radius * (surface ? 1 : Math.cbrt(this.random())));
      if (cylinder && t.startheight) offset.y += (this.random() - 0.5) * sample(t.startheight, this.emitTime, rnd());
      p.pos.add(offset);
      if ((sphere ?? cylinder).values.velocity) {
        const vscale = t.velocityscale ? sample(t.velocityscale, this.emitTime, rnd()) : 1;
        // The engine's sphere and cylinder push outward by the offset itself; Lost Ark's circle surface by its direction.
        p.vel.add(direction.multiplyScalar(sphere || !cylinder.class.includes('circlesurface') ? radius * vscale : vscale));
      }
    }
    if (t.startvelocityradial) {
      const radial = sample(t.startvelocityradial, this.emitTime, rnd());
      if (p.pos.lengthSq() > 0) p.vel.add(p.pos.clone().normalize().multiplyScalar(radial));
    }
    this.particles.push(p);
  }

  update(dt, effectTime) {
    const local = effectTime - this.delay;
    if (local >= 0 && !this.done) {
      const loopTime = local % this.duration;
      const loop = Math.floor(local / this.duration);
      if (loop !== this.loopIndex) { this.loopIndex = loop; this.burstsFired.clear(); }
      if (this.loops > 0 && loop >= this.loops) this.done = true;
      else {
        this.emitTime = loopTime;
        this.spawnCarry += this.spawnRate(loopTime) * dt;
        while (this.spawnCarry >= 1) { this.spawn(); this.spawnCarry -= 1; }
        this.bursts.forEach((burst, i) => {
          if (!this.burstsFired.has(i) && loopTime / this.duration >= burst.time) {
            this.burstsFired.add(i);
            const count = burst.low >= 0 ? burst.low + Math.floor(this.random() * (burst.count - burst.low + 1)) : burst.count;
            for (let n = 0; n < count; n++) this.spawn();
          }
        });
      }
    }
    const t = this.tables;
    const acceleration = t.acceleration ? toThree(sample(t.acceleration, 0, [0.5, 0.5, 0.5])) : null;
    this.particles = this.particles.filter((p) => {
      p.age += dt;
      if (p.age >= p.life) return false;
      if (acceleration) p.vel.addScaledVector(acceleration, dt);
      p.pos.addScaledVector(p.vel, dt);
      p.rot += p.rotRate * dt;
      return true;
    });
  }

  write() {
    const count = this.visible ? this.particles.length : 0;
    const t = this.tables;
    for (let i = 0; i < count; i++) {
      const p = this.particles[i];
      const life = p.age / p.life;
      let color = t.coloroverlife ? sample(t.coloroverlife, life, p.seeds) : p.color;
      let alpha = t.alphaoverlife ? sample(t.alphaoverlife, life, p.seeds) : p.alpha;
      let sx = p.size[0], sy = p.size[1];
      color = color.slice();
      for (const { table } of this.lists.colorscaleoverlife) {
        const scale = sample(table, life, p.seeds);
        color[0] *= scale[0]; color[1] *= scale[1]; color[2] *= scale[2];
      }
      for (const { table } of this.lists.alphascaleoverlife) alpha *= sample(table, life, p.seeds);
      for (const { table, module } of this.lists.lifemultiplier) {
        const m = sample(table, life, p.seeds);
        if (module.values.multiplyx !== false) sx *= m[0];
        if (module.values.multiplyy !== false) sy *= m[1];
      }
      this.positions.set([p.pos.x, p.pos.y, p.pos.z], i * 3);
      this.colors.set([Math.max(0, color[0]), Math.max(0, color[1]), Math.max(0, color[2]), Math.min(1, Math.max(0, alpha))], i * 4);
      this.sizes[i] = Math.max(Math.abs(sx), Math.abs(sy));
      this.rotations[i] = p.rot;
    }
    const geometry = this.points.geometry;
    geometry.setDrawRange(0, count);
    for (const name of ['position', 'aColor', 'aSize', 'aRot']) geometry.attributes[name].needsUpdate = true;
  }

  buildMaterial() {
    const geometry = new THREE.BufferGeometry();
    this.positions = new Float32Array(MAX_PARTICLES * 3);
    this.colors = new Float32Array(MAX_PARTICLES * 4);
    this.sizes = new Float32Array(MAX_PARTICLES);
    this.rotations = new Float32Array(MAX_PARTICLES);
    geometry.setAttribute('position', new THREE.BufferAttribute(this.positions, 3).setUsage(THREE.DynamicDrawUsage));
    geometry.setAttribute('aColor', new THREE.BufferAttribute(this.colors, 4).setUsage(THREE.DynamicDrawUsage));
    geometry.setAttribute('aSize', new THREE.BufferAttribute(this.sizes, 1).setUsage(THREE.DynamicDrawUsage));
    geometry.setAttribute('aRot', new THREE.BufferAttribute(this.rotations, 1).setUsage(THREE.DynamicDrawUsage));
    geometry.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 1e9);
    this.material = new THREE.ShaderMaterial({
      uniforms: { map: { value: null }, hasMap: { value: 0 }, pixelScale: { value: 500 } },
      vertexShader: `
        attribute vec4 aColor; attribute float aSize; attribute float aRot;
        uniform float pixelScale; varying vec4 vColor; varying float vRot;
        void main() {
          vec4 mv = modelViewMatrix * vec4(position, 1.0);
          gl_Position = projectionMatrix * mv;
          gl_PointSize = clamp(aSize * pixelScale / max(1.0, -mv.z), 0.0, 2048.0);
          vColor = aColor; vRot = aRot;
        }`,
      fragmentShader: `
        uniform sampler2D map; uniform float hasMap; varying vec4 vColor; varying float vRot;
        void main() {
          vec2 c = gl_PointCoord - 0.5;
          float s = sin(vRot), k = cos(vRot);
          vec2 uv = vec2(k * c.x - s * c.y, s * c.x + k * c.y) + 0.5;
          float edge = smoothstep(0.5, 0.3, length(c));
          vec4 tex = hasMap > 0.5 ? texture2D(map, uv) : vec4(1.0);
          // Exported effect textures are mostly grey masks without alpha: their brightness is the coverage. The soft
          // round edge hides the square of masks that do not fade out at their borders.
          float mask = (hasMap > 0.5 ? tex.a * max(tex.r, max(tex.g, tex.b)) : 1.0) * edge;
          float a = vColor.a * mask;
          if (a <= 0.002) discard;
          // The game's colours are HDR (often far above 1); roll them off so bright effects keep their hue.
          vec3 hdr = vColor.rgb * (hasMap > 0.5 ? tex.rgb / max(0.001, max(tex.r, max(tex.g, tex.b))) : vec3(1.0));
          gl_FragColor = vec4(vec3(1.0) - exp(-hdr * 0.8), a);
        }`,
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    });
    this.points = new THREE.Points(geometry, this.material);
    this.points.frustumCulled = false;
    scene.add(this.points);
    this.applyTexture();
  }

  applyTexture() {
    const found = state.textures[this.materialName ?? ''];
    if (!found) return;
    this.material.blending = found.blend === 'additive' ? THREE.AdditiveBlending : THREE.NormalBlending;
    this.material.needsUpdate = true;
    // A mesh emitter's texture wraps its mesh, not a sprite, so the preview draws those as soft glows.
    if (!found.url || this.isMesh) return;
    // The textures come from another virtual host; without CORS the browser would hand WebGL a blank image.
    new THREE.TextureLoader().setCrossOrigin('anonymous').load(found.url, (texture) => {
      texture.colorSpace = THREE.SRGBColorSpace;
      this.material.uniforms.map.value = texture;
      this.material.uniforms.hasMap.value = 1;
    });
  }

  dispose() {
    scene.remove(this.points);
    this.points.geometry.dispose();
    this.material.dispose();
  }
}

// ---- Playback ----
function effectLength() {
  const ends = state.emitters.map((emitter) => emitter.finishedAt);
  const finite = ends.filter(Number.isFinite);
  return finite.length === ends.length && finite.length ? Math.max(...finite) : 3;
}

function restart() {
  state.time = 0;
  state.emitters.forEach((emitter) => emitter.reset());
}

function tick(now) {
  const dt = state.last ? Math.min(0.05, (now - state.last) / 1000) : 0;
  state.last = now;
  if (state.playing && state.emitters.length) {
    const step = dt * state.speed;
    state.time += step;
    state.emitters.forEach((emitter) => emitter.update(step, state.time));
    const alive = state.emitters.some((emitter) => emitter.particles.length);
    if (state.loop && state.time > effectLength() && !alive) restart();
  }
  state.emitters.forEach((emitter) => emitter.write());
  $('timeText').textContent = state.emitters.length ? `${state.time.toFixed(2)} s · ${state.emitters.reduce((n, e) => n + (e.visible ? e.particles.length : 0), 0)} particles` : '';
  resize();
  renderer.render(scene, camera);
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
  const pixelScale = height / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)));
  state.emitters.forEach((emitter) => { emitter.material.uniforms.pixelScale.value = pixelScale; });
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
  };
  for (const [key] of MULTIPLIERS) recipe[key] = +$(`m-${key}`).value;
  return recipe;
}

function showRecipe(recipe) {
  $('tintCheck').checked = !!recipe.tint;
  const tint = recipe.tint ?? { red: 1, green: 1, blue: 1 };
  $('tintRed').value = tint.red; $('tintGreen').value = tint.green; $('tintBlue').value = tint.blue;
  $('tintStrength').value = recipe.tintStrength ?? 1;
  for (const [key] of MULTIPLIERS) $(`m-${key}`).value = recipe[key] ?? 1;
  refreshOutputs();
}

function refreshOutputs() {
  for (const id of ['tintRed', 'tintGreen', 'tintBlue']) $(`${id}Text`).textContent = (+$(id).value).toFixed(2);
  $('tintStrengthText').textContent = `${Math.round(+$('tintStrength').value * 100)}%`;
  for (const [key] of MULTIPLIERS) $(`m-${key}Text`).textContent = `${(+$(`m-${key}`).value).toFixed(2)}×`;
  const [r, g, b] = ['tintRed', 'tintGreen', 'tintBlue'].map((id) => Math.round(Math.pow(+$(id).value, 1 / 2.2) * 255));
  $('tintPreview').style.background = $('tintCheck').checked ? `rgb(${r},${g},${b})` : 'transparent';
}

function recipeChanged() {
  refreshOutputs();
  state.recipe = readRecipe();
  state.emitters.forEach((emitter) => emitter.applyRecipe(state.recipe));
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
  for (const input of document.querySelectorAll('#panel input[type="range"], #tintCheck')) input.addEventListener('input', recipeChanged);
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
    detail.textContent = [emitter.isMesh ? 'mesh' : null, emitter.model.enabled ? null : 'off in game', emitter.materialName].filter(Boolean).join(' · ');
    label.title = detail.textContent;
    label.append(detail);
    list.appendChild(label);
  }
}

function load(message) {
  if (message.theme) document.documentElement.dataset.theme = message.theme;
  if (message.theme === 'light') { scene.background = new THREE.Color(backgrounds.light); $('backgroundSelect').value = 'light'; }
  state.emitters.forEach((emitter) => emitter.dispose());
  state.model = message.model;
  state.recipe = { ...defaultRecipe(), ...(message.recipe ?? {}) };
  showRecipe(state.recipe);
  state.emitters = message.model.emitters.map((model, index) => new Emitter(model, index));
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
  else if (message?.type === 'textures') {
    Object.assign(state.textures, message.items ?? {});
    state.emitters.forEach((emitter) => emitter.applyTexture());
  } else if (message?.type === 'status') $('status').textContent = message.text;
  else if (message?.type === 'error') { $('loading').style.display = ''; $('loading').textContent = message.text; }
});

buildControls();
refreshOutputs();
requestAnimationFrame(tick);
post({ type: 'ready' });
