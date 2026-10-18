// Arkive's particle simulation, shared by the particle effect window and the model viewer's skill preview. It plays a
// particle system's emitters from the settings Arkive read out of the game package (the baked value tables the game
// itself samples) with the engine's defaults for settings the package leaves out, and draws them the way the game
// does where it can: camera-facing or velocity-aligned sprites with flipbook frames, flat decals, and mesh particles.
// Materials are approximated by their first texture and blend mode.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

export const MAX_PARTICLES = 4000;

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
export function sample(table, time, random) {
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

// A setting handed in by whoever starts the effect (see ParticleParameterModel): mapped from the value a skill
// passes under its name, or the distribution's constant when none is passed.
function sampleParameter(model, time, random, parameters) {
  const given = parameters?.[model.name?.toLowerCase()];
  const dims = model.dimensions;
  const out = new Array(dims);
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
    out[d] = value;
  }
  if (model.multiply && model.base) {
    const base = sample(model.base, time, random);
    return dims === 1 ? base * out[0] : base.map((value, d) => value * out[d]);
  }
  return dims === 1 ? out[0] : out;
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

// ---- Assets: textures and meshes by material and mesh name, loaded once and shared ----
export class EffectAssets {
  constructor() {
    this.textures = {};
    this.meshes = {};
    this.loadedTextures = new Map();
    this.loadedMeshes = new Map();
    this.listeners = new Set();
    this.textureLoader = new THREE.TextureLoader().setCrossOrigin('anonymous');
    this.gltfLoader = new GLTFLoader().setCrossOrigin('anonymous');
  }

  add({ textures, meshes }) {
    Object.assign(this.textures, textures ?? {});
    Object.assign(this.meshes, meshes ?? {});
    this.listeners.forEach((listener) => listener());
  }

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
}

// ---- Shaders ----
const SPRITE_VERTEX = `
  attribute vec3 iPos; attribute vec2 iSize; attribute vec4 iColor; attribute float iRot; attribute vec3 iVel; attribute float iFrame;
  uniform float alignMode; uniform vec2 subImages;
  varying vec4 vColor; varying vec2 vUv; varying vec2 vLocal; varying float vFacing;
  void main() {
    vec2 corner = position.xy;
    vLocal = corner;
    vFacing = 1.0;
    vec4 center;
    if (alignMode > 1.5) {
      // A decal: lies flat on the ground under the particle.
      float s = sin(iRot), c = cos(iRot);
      vec2 r = vec2(c * corner.x - s * corner.y, s * corner.x + c * corner.y) * iSize;
      center = modelViewMatrix * vec4(iPos + vec3(r.x, 0.5, r.y), 1.0);
    } else {
      center = modelViewMatrix * vec4(iPos, 1.0);
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
        float s = sin(iRot), c = cos(iRot);
        vec2 scaled = corner * iSize;
        offset = vec2(c * scaled.x - s * scaled.y, s * scaled.x + c * scaled.y);
      }
      center.xy += offset * unit;
    }
    gl_Position = projectionMatrix * center;
    vColor = iColor;
    // Flipbook frames count from the top left, as the game lays them out.
    float columns = max(1.0, subImages.x), rows = max(1.0, subImages.y);
    float frame = mod(floor(iFrame), columns * rows);
    vec2 cell = vec2(mod(frame, columns), floor(frame / columns));
    vec2 uv = corner + 0.5;
    vUv = vec2((cell.x + uv.x) / columns, 1.0 - (cell.y + 1.0 - uv.y) / rows);
  }`;

const MESH_VERTEX = `
  attribute vec3 iPos; attribute vec3 iScale; attribute vec4 iQuat; attribute vec4 iColor;
  varying vec4 vColor; varying vec2 vUv; varying vec2 vLocal; varying float vFacing;
  vec3 rotate(vec4 q, vec3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
  void main() {
    vec3 local = rotate(iQuat, position * iScale);
    vec4 view = modelViewMatrix * vec4(iPos + local, 1.0);
    gl_Position = projectionMatrix * view;
    vec3 n = normalize(normalMatrix * rotate(iQuat, normal / max(iScale, vec3(1e-4))));
    vFacing = abs(dot(n, normalize(-view.xyz)));
    vColor = iColor; vUv = uv; vLocal = vec2(0.0);
  }`;

const FRAGMENT = `
  uniform sampler2D map; uniform float hasMap; uniform float roundEdge;
  uniform float texturePower; uniform float strength; uniform float fresnelPower; uniform float fresnelStrength;
  varying vec4 vColor; varying vec2 vUv; varying vec2 vLocal; varying float vFacing;
  void main() {
    vec4 tex = hasMap > 0.5 ? texture2D(map, vUv) : vec4(1.0);
    float edge = roundEdge > 0.5 ? smoothstep(0.5, 0.3, length(vLocal)) : 1.0;
    // Exported effect textures are mostly grey masks without alpha: their brightness is the coverage, sharpened by
    // the material's power and scaled by its strength.
    float strongest = max(tex.r, max(tex.g, tex.b));
    float mask = (hasMap > 0.5 ? tex.a * pow(strongest, texturePower) : 1.0) * edge * strength;
    // Fresnel materials show mostly towards a mesh's edges.
    if (fresnelStrength > 0.0) mask *= clamp(pow(1.0 - vFacing, max(0.25, fresnelPower)) * fresnelStrength, 0.0, 1.0);
    float a = vColor.a * mask;
    if (a <= 0.002) discard;
    // The game's colours are HDR (often far above 1); roll them off so bright effects keep their hue.
    vec3 hdr = vColor.rgb * (hasMap > 0.5 ? tex.rgb / max(0.001, strongest) : vec3(1.0));
    gl_FragColor = vec4(vec3(1.0) - exp(-hdr * 0.8), a);
  }`;

const QUAD = (() => {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute('position', new THREE.Float32BufferAttribute([-0.5, -0.5, 0, 0.5, -0.5, 0, 0.5, 0.5, 0, -0.5, 0.5, 0], 3));
  geometry.setIndex([0, 1, 2, 0, 2, 3]);
  return geometry;
})();

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
    // Light emitters light the scene and distortion materials bend what is behind them; neither is drawn as particles.
    this.isLight = model.modules.some((module) => module.class.includes('typedatalight'));
    this.isDistortion = /(^|_)(dist|distort|circledisort|worldoffset)(_|\d)|defaultparticle/.test(model.material ?? '') ||
      /distort|(^|[_-])dist([_-]|$)/i.test(model.name ?? '');
    if (this.isLight || this.isDistortion) this.visible = false;
    this.alignMode = this.isDecal ? 2 : alignment === 'psa_velocity' ? 1 : 0;
    this.squareSprites = alignment === 'psa_square' || alignment === 'psa_typespecific';
    this.particles = [];
    this.random = Math.random;
    this.buildRenderer();
    this.applyRecipe(effect.recipe);
    this.reset();
  }

  module(fragment) { return this.model.modules.find((module) => module.class.includes(fragment)); }

  applyRecipe(recipe) {
    // Each setting is the first table of that name among the modules; over-life multipliers stack.
    const tables = {};
    const lists = { lifemultiplier: [], colorscaleoverlife: [], alphascaleoverlife: [] };
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

  value(table, time, random) {
    return table.modes ? sampleParameter(table, time, random, this.effect.scales.named) : sample(table, time, random);
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
    // An emitter whose spawn rate the package leaves out emits the engine's default 20 a second.
    let value = rate ? this.value(rate, time, [0.5]) : 20 * this.effect.recipe.spawnRate;
    if (this.tables.ratescale) value *= this.value(this.tables.ratescale, time / this.duration, [0.5]);
    return Math.max(0, value * this.effect.scales.spawn);
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
    const location = t.startlocation ? this.value(t.startlocation, time, rnd()) : [0, 0, 0];
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
    };
    if (this.isMesh && t.startrotation && !Array.isArray(this.value(t.startrotation, time, [0.5]))) p.meshRot.y = p.rot;
    const sphere = this.module('locationprimitivesphere');
    const cylinder = this.module('locationprimitivecylinder') ?? this.module('circlesurface');
    if (sphere || cylinder) {
      const radius = t.startradius ? Math.abs(this.value(t.startradius, time, rnd())) : 50;
      const direction = new THREE.Vector3();
      if (sphere) direction.randomDirection();
      else { const a = this.random() * Math.PI * 2; direction.set(Math.cos(a), 0, Math.sin(a)); }
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
        else this.particles = this.particles.filter((p) => Number.isFinite(p.life));
      } else {
        this.emitTime = loopTime;
        this.spawnCarry += this.spawnRate(loopTime) * dt;
        while (this.spawnCarry >= 1) { this.spawn(); this.spawnCarry -= 1; }
        this.bursts.forEach((burst, i) => {
          if (!this.burstsFired.has(i) && loopTime / this.duration >= burst.time) {
            this.burstsFired.add(i);
            const count = burst.low >= 0 ? burst.low + Math.floor(this.random() * (burst.count - burst.low + 1)) : burst.count;
            for (let n = 0; n < count * Math.max(this.effect.scales.spawn, 0); n++) this.spawn();
          }
        });
      }
    }
    const t = this.tables;
    const acceleration = t.acceleration ? toThree(this.value(t.acceleration, 0, [0.5, 0.5, 0.5])) : null;
    this.particles = this.particles.filter((p) => {
      p.age += dt;
      if (p.age >= p.life) return false;
      if (acceleration) p.vel.addScaledVector(acceleration, dt * (p.worldScale ?? 1));
      p.pos.addScaledVector(p.vel, dt);
      p.rot += p.rotRate * dt;
      if (this.isMesh) p.meshRot.addScaledVector(p.meshRotRate, dt);
      return true;
    });
  }

  write() {
    const count = this.visible ? Math.min(this.particles.length, MAX_PARTICLES) : 0;
    const t = this.tables, s = this.effect.scales;
    const frames = this.subImages[0] * this.subImages[1];
    const euler = new THREE.Euler(), quat = new THREE.Quaternion();
    for (let i = 0; i < count; i++) {
      const p = this.particles[i];
      // Settings over life read the particle's relative age; ones that never die stay at their start.
      const life = Number.isFinite(p.life) ? p.age / p.life : 0;
      let color = t.coloroverlife ? this.value(t.coloroverlife, life, p.seeds) : p.color;
      let alpha = t.alphaoverlife ? this.value(t.alphaoverlife, life, p.seeds) : p.alpha;
      const size = p.size.slice();
      const tint = this.materialTint ?? WHITE;
      color = [color[0] * s.color[0] * tint[0], color[1] * s.color[1] * tint[1], color[2] * s.color[2] * tint[2]];
      alpha *= s.alpha;
      for (const { table } of this.lists.colorscaleoverlife) {
        const scale = this.value(table, life, p.seeds);
        color[0] *= scale[0]; color[1] *= scale[1]; color[2] *= scale[2];
      }
      for (const { table } of this.lists.alphascaleoverlife) alpha *= this.value(table, life, p.seeds);
      for (const { table, module } of this.lists.lifemultiplier) {
        const m = this.value(table, life, p.seeds);
        if (module.values.multiplyx !== false) size[0] *= Array.isArray(m) ? m[0] : m;
        if (module.values.multiplyy !== false) size[1] *= Array.isArray(m) ? m[1] : m;
        if (module.values.multiplyz !== false) size[2] *= Array.isArray(m) ? m[2] : m;
      }
      this.positions.set([p.pos.x, p.pos.y, p.pos.z], i * 3);
      this.colors.set([Math.max(0, color[0]), Math.max(0, color[1]), Math.max(0, color[2]), Math.min(1, Math.max(0, alpha))], i * 4);
      if (this.drawMesh) {
        // Unreal sizes are X, Y, Z with Z up.
        this.scales.set([size[0], size[2], size[1]], i * 3);
        quat.setFromEuler(euler.set(p.meshRot.x, p.meshRot.y, p.meshRot.z, 'YZX'));
        this.quats.set([quat.x, quat.y, quat.z, quat.w], i * 4);
      } else {
        this.sizes.set(this.squareSprites ? [size[0], size[0]] : [size[0], size[1]], i * 2);
        this.rotations[i] = p.rot;
        this.velocities.set([p.vel.x, p.vel.y, p.vel.z], i * 3);
        let frame = p.frame;
        if (frames > 1 && (this.interpolation === 'psuvim_linear' || this.interpolation === 'psuvim_linear_blend')) frame = Math.floor(life * frames);
        if (t.subimageindex) frame = Math.floor(this.value(t.subimageindex, life, p.seeds));
        this.frames[i] = frame;
      }
    }
    this.geometry.instanceCount = count;
    for (const attribute of Object.values(this.geometry.attributes)) if (attribute.isInstancedBufferAttribute) attribute.needsUpdate = true;
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
    const instanced = (name, size) => {
      const array = new Float32Array(MAX_PARTICLES * size);
      geometry.setAttribute(name, new THREE.InstancedBufferAttribute(array, size).setUsage(THREE.DynamicDrawUsage));
      return array;
    };
    this.positions = instanced('iPos', 3);
    this.colors = instanced('iColor', 4);
    const drawMesh = this.drawMesh = !!meshGeometry;
    if (drawMesh) {
      this.scales = instanced('iScale', 3);
      this.quats = instanced('iQuat', 4);
    } else {
      this.sizes = instanced('iSize', 2);
      this.rotations = instanced('iRot', 1);
      this.velocities = instanced('iVel', 3);
      this.frames = instanced('iFrame', 1);
    }
    geometry.instanceCount = 0;
    this.geometry = geometry;
    this.material = new THREE.ShaderMaterial({
      uniforms: {
        map: { value: null }, hasMap: { value: 0 }, alignMode: { value: this.alignMode },
        subImages: { value: new THREE.Vector2(this.subImages[0], this.subImages[1]) },
        roundEdge: { value: drawMesh ? 0 : 1 },
        texturePower: { value: 1 }, strength: { value: 1 }, fresnelPower: { value: 1 }, fresnelStrength: { value: 0 },
      },
      vertexShader: drawMesh ? MESH_VERTEX : SPRITE_VERTEX,
      fragmentShader: FRAGMENT,
      transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
    });
    this.object = new THREE.Mesh(geometry, this.material);
    this.object.frustumCulled = false;
    (this.localSpace || !this.effect.parent ? this.effect.group : this.effect.worldGroup).add(this.object);
    this.applyAssets();
  }

  applyAssets() {
    const assets = this.effect.assets;
    if (!assets) return;
    if (this.isMesh && !this.meshRequested && assets.mesh(this.meshName, (geometry) => {
      if (geometry) { this.buildRenderer(geometry); }
    })) this.meshRequested = true;
    const found = assets.textures[this.materialName ?? ''];
    if (!found) return;
    this.materialTint = found.tint ?? null;
    const scalars = found.scalars ?? {};
    // Distortion materials bend the image behind them; the preview leaves them out.
    if (scalars.distortion > 0 && found.blend !== 'additive') { this.isDistortion = true; this.visible = false; }
    const uniforms = this.material.uniforms;
    uniforms.texturePower.value = scalars.power > 0 ? Math.min(scalars.power, 8) : 1;
    uniforms.strength.value = scalars.strength > 0 ? Math.min(scalars.strength, 4) : 1;
    uniforms.fresnelStrength.value = this.isMesh && scalars.fresnelStrength > 0 ? scalars.fresnelStrength : 0;
    uniforms.fresnelPower.value = scalars.fresnelPower > 0 ? scalars.fresnelPower : 1;
    this.material.blending = found.blend === 'additive' ? THREE.AdditiveBlending : THREE.NormalBlending;
    this.material.needsUpdate = true;
    // Mesh particles draw a texture only once their mesh (and so its UVs) is loaded.
    if (this.isMesh && !this.material.vertexShader.includes('iQuat')) return;
    assets.texture(this.materialName, (texture) => {
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
  constructor({ model, root, parent = null, assets = null, recipe = defaultRecipe(), parameters = null }) {
    this.model = model;
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
    for (const emitter of this.emitters) emitter.particles = emitter.particles.filter((p) => Number.isFinite(p.life));
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
