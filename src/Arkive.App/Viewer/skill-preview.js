// The model viewer's skill preview: the shown character plays one of its class's skills as the game's action data
// describes it. Each stage plays its animation; each effect the skill starts appears at its time, follows its socket
// (or bone) and takes the colour, size and other values the skill hands it.
import * as THREE from 'three';
import { ParticleEffect, EffectAssets, ueRotator as rotator } from './particle-sim.js';

const escapeHtml = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// How long effects may play on after the last stage, so their particles can finish.
const TAIL = 2.5;

// A skill lists the same moment's effects once per effect set (FX-00, FX-01, FX-Exmove...): the game plays one set,
// FX-00 unless the character's build calls for another, along with the effects every set shares (FX, or none).
const isOptional = (group) => /^fx-./i.test(group ?? '');
const DEFAULT_SET = 'fx-00';

export function createSkillPreview(context) {
  const { world, $, post, requestRender, model, scene, socketOffset, animation, renderer, camera, ground } = context;
  const tab = document.querySelector('[data-tab="skills"]');
  const panel = $('tab-skills');
  const ui = { offer: null, loadingOffer: false, classFile: '', skill: 0, branch: 0, effectSet: DEFAULT_SET, progress: null };
  const assets = new EffectAssets();
  const player = { data: null, clips: new Map(), time: 0, playing: false, speed: 1, loop: true, effects: [], fired: new Set(), last: 0, stages: [], weapons: [],
    warm: null, frames: 0, sets: [] };

  // Whether the chosen effect set plays a notify.
  function plays(notify) { return !isOptional(notify.group) || notify.group.toLowerCase() === ui.effectSet; }
  window.arkiveSkills = { ui, player };

  function render() {
    const offer = ui.offer;
    if (!offer) {
      panel.innerHTML = ui.loadingOffer
        ? `<p class="muted">Reading the game's skill list…</p><progress class="skill-progress"></progress>` : '';
      return;
    }
    const classes = offer.classes;
    const current = classes.find((item) => item.file === ui.classFile) ?? classes[0];
    const skill = current?.skills.find((item) => item.id === ui.skill) ?? current?.skills[0];
    ui.classFile = current?.file ?? '';
    ui.skill = skill?.id ?? 0;
    ui.branch = Math.min(ui.branch, Math.max(0, (skill?.branches.length ?? 1) - 1));
    panel.innerHTML = `
      <p class="muted">Plays a skill as the game's own skill data describes it: its animations, and each effect at its time and
        socket with the colours and sizes the skill gives it. Effects are drawn with their materials' own game shaders where the
        game's shader cache has them.</p>
      <label class="field">Class <select id="skillClass">${classes.map((item) =>
        `<option value="${escapeHtml(item.file)}"${item.file === ui.classFile ? ' selected' : ''}>${escapeHtml(item.name)}</option>`).join('')}</select></label>
      <label class="field">Skill <select id="skillSelect">${(current?.skills ?? []).map((item) =>
        `<option value="${item.id}"${item.id === ui.skill ? ' selected' : ''}>${escapeHtml(item.name)}</option>`).join('')}</select></label>
      ${skill && skill.branches.length > 1 ? `<label class="field">Version <select id="skillBranch">${skill.branches.map((name, index) =>
        `<option value="${index}"${index === ui.branch ? ' selected' : ''}>${index + 1}. ${escapeHtml(name)}</option>`).join('')}</select></label>` : ''}
      <div class="skill-row">
        <button id="skillPlay">${player.playing ? 'Stop' : 'Play skill'}</button>
        <label class="field">Speed <select id="skillSpeed">${[0.1, 0.25, 0.5, 1].map((v) =>
          `<option value="${v}"${v === player.speed ? ' selected' : ''}>${v}×</option>`).join('')}</select></label>
        <label class="field"><input type="checkbox" id="skillLoop"${player.loop ? ' checked' : ''}> Loop</label>
      </div>
      ${player.sets.length > 1 ? `<label class="field">Effects <select id="skillSet">${player.sets.map((set) =>
        `<option value="${escapeHtml(set)}"${set === ui.effectSet ? ' selected' : ''}>${set === DEFAULT_SET ? 'Standard (FX-00)' : escapeHtml(set.toUpperCase())}</option>`).join('')}</select></label>` : ''}
      <progress id="skillBar" class="skill-progress" max="1"${ui.progress == null ? ' hidden' : ''}></progress>
      <div id="skillStatus" class="muted"></div>
      <div id="skillTimeline" class="mono"></div>`;
    $('skillClass').addEventListener('change', (e) => { ui.classFile = e.target.value; ui.skill = 0; ui.branch = 0; render(); });
    $('skillSelect').addEventListener('change', (e) => { ui.skill = Number(e.target.value); ui.branch = 0; ui.effectSet = DEFAULT_SET; render(); });
    $('skillBranch')?.addEventListener('change', (e) => { ui.branch = Number(e.target.value); });
    $('skillSpeed').addEventListener('change', (e) => { player.speed = Number(e.target.value); });
    $('skillLoop').addEventListener('change', (e) => { player.loop = e.target.checked; });
    $('skillSet')?.addEventListener('change', (e) => { ui.effectSet = e.target.value; restart(); renderTimeline(); });
    $('skillPlay').addEventListener('click', () => {
      if (player.playing) { stop(); return; }
      // The skill already loaded plays again straight away.
      const key = `${ui.classFile}|${ui.skill}|${ui.branch}`;
      if (player.data && player.loadedKey === key) { replay(); return; }
      player.pendingKey = key;
      progress('Preparing the skill…', 0);
      post({ type: 'playSkill', classFile: ui.classFile, skill: ui.skill, branch: ui.branch });
    });
    renderTimeline();
  }

  function status(text) { progress(text, null); }

  // The status line and, while something loads, a bar (fraction 0-1; undefined for one of unknown length).
  function progress(text, fraction) {
    ui.progress = fraction;
    const element = $('skillStatus');
    if (element) element.textContent = text;
    const bar = $('skillBar');
    if (!bar) return;
    bar.hidden = fraction === null;
    if (fraction === undefined) bar.removeAttribute('value');
    else if (fraction !== null) bar.value = Math.max(0, Math.min(1, fraction));
  }

  function renderTimeline() {
    const element = $('skillTimeline');
    if (!element || !player.data) return;
    element.innerHTML = player.stages.map((stage, index) => `<div><b>${index + 1}. ${escapeHtml(stage.anim ?? 'no animation')}</b>
      ${stage.duration.toFixed(2)} s</div>` + stage.notifies.filter(plays).map((notify) =>
      `<div>&nbsp;&nbsp;${notify.start.toFixed(2)} s · ${escapeHtml(notify.system.split('.').pop())}${notify.socket ? ` @ ${escapeHtml(notify.socket)}` : ''}</div>`).join('')).join('');
  }

  // ---- Playback ----
  function bonesIn(root, attached) {
    const bones = new Map();
    const visit = (object, inAttached) => {
      const here = inAttached || !!object.userData.attached;
      if (object.isBone && here === attached) {
        const name = object.name.replace(/__ARKIVE_\d+$/, '').toLowerCase();
        if (!bones.has(name)) bones.set(name, object);
      }
      for (const child of object.children) visit(child, here);
    };
    if (root) visit(root, attached && !!root.userData.attached);
    return bones;
  }

  // Where a socket is: its bone, offset and rotation. The character's own sockets come first, then its weapons'.
  function findSocket(name) {
    const lower = name.toLowerCase();
    // The shown mesh's sockets, then the class skeleton's (sent as a weapon entry without a part).
    const bodySockets = [...(scene()?.sockets ?? []), ...(player.data?.weapons ?? []).filter((item) => !item.part).flatMap((item) => item.sockets ?? [])];
    for (const body of bodySockets.filter((item) => item.name.toLowerCase() === lower)) {
      const bone = bonesIn(model(), false).get(body.bone.toLowerCase());
      if (bone) return { bone, socket: body };
    }
    for (const weapon of player.weapons) {
      const socket = weapon.sockets?.find((item) => item.name.toLowerCase() === lower);
      const bone = socket ? bonesIn(weapon.root, true).get(socket.bone.toLowerCase()) : null;
      if (bone) return { bone, socket };
    }
    // Some effects name a bone instead (B_Wing_Root, B_EffectRoot).
    const bone = bonesIn(model(), false).get(lower) ?? player.weapons.map((weapon) => bonesIn(weapon.root, true).get(lower)).find(Boolean);
    return bone ? { bone, socket: null } : null;
  }

  // Puts the class's weapons in the character's hand: each weapon part of the scene goes in its battle socket.
  function attachWeapons(weapons) {
    player.weapons = [];
    const root = model();
    if (!root) return;
    for (const weapon of (weapons ?? []).filter((item) => item.part)) {
      const part = root.children.find((child) => child.userData.partName?.toLowerCase() === weapon.part.toLowerCase());
      const holder = findSocket(weapon.socket);
      if (!part) continue;
      // A weapon whose socket the skeleton lacks would hang at the character's feet.
      part.visible = !!holder;
      if (!holder) continue;
      part.userData.attached = true;
      holder.bone.add(part);
      part.position.copy(holder.socket ? socketOffset(holder.socket) : new THREE.Vector3());
      part.quaternion.copy(holder.socket ? rotator(holder.socket.rotation, 65536) : new THREE.Quaternion());
      part.scale.setScalar(1);
      player.weapons.push({ ...weapon, root: part });
    }
    requestRender();
  }

  // The object an effect follows: the named socket or bone, else the character itself, moved by the effect's own
  // offset, rotation and scale. Its scale also turns the effect's Unreal centimetres into the viewer's metres.
  function attachment(notify) {
    const root = model();
    if (!root) return null;
    let parent = root;
    const object = new THREE.Object3D();
    const holder = notify.socket ? findSocket(notify.socket) : null;
    if (holder) {
      parent = holder.bone;
      if (holder.socket) {
        object.position.copy(socketOffset(holder.socket));
        object.quaternion.copy(rotator(holder.socket.rotation, 65536));
      }
    }
    parent.add(object);
    parent.updateWorldMatrix(true, false);
    const scale = new THREE.Vector3().setFromMatrixScale(parent.matrixWorld).x || 1;
    const units = 0.01 / scale;
    const effectScale = notify.scale ?? [1, 1, 1];
    // The offset is in the socket's frame, in Unreal units; the effect's rotation turns it within that frame.
    if (notify.offset) {
      const [x, y, z] = notify.offset;
      object.position.add(new THREE.Vector3(x, z, y).multiplyScalar(units).applyQuaternion(object.quaternion));
    }
    if (notify.rotation) object.quaternion.multiply(rotator(notify.rotation));
    object.scale.set(units * effectScale[0], units * effectScale[2], units * effectScale[1]);
    return object;
  }

  async function start(data) {
    stop();
    player.data = data;
    player.loadedKey = player.pendingKey;
    attachWeapons(data.weapons);
    assets.add({ textures: data.assets?.items, meshes: data.assets?.meshes, prelude: data.assets?.prelude });
    prewarm(data);
    let at = 0;
    // Stages with neither an animation nor effects are the game waiting on the player (a held key, a follow-up press);
    // the preview moves straight on.
    player.stages = data.stages.filter((stage) => stage.anim || stage.notifies.length).map((stage) => {
      const begins = at;
      at += Math.max(0.05, stage.duration);
      return { ...stage, begins };
    });
    player.length = at;
    const sets = new Set(data.stages.flatMap((stage) => stage.notifies.filter((notify) => isOptional(notify.group))
      .map((notify) => notify.group.toLowerCase())));
    if (sets.size) sets.add(DEFAULT_SET);
    player.sets = [...sets].sort();
    if (!player.sets.includes(ui.effectSet)) ui.effectSet = DEFAULT_SET;
    renderTimeline();
    player.clips.clear();
    const list = data.animations.list;
    const wanted = [...new Set(player.stages.map((stage) => stage.anim).filter(Boolean))];
    for (const [index, anim] of wanted.entries()) {
      progress(`Loading ${data.name}'s animations (${index + 1} of ${wanted.length})…`, 0.95 + 0.05 * index / wanted.length);
      const info = list.find((item) => item.name.toLowerCase() === anim.toLowerCase());
      if (info) player.clips.set(anim, await animation.load(data.animations.baseUrl, data.animations.bones, info));
    }
    const missing = player.stages.filter((stage) => stage.anim && !player.clips.has(stage.anim)).length;
    status(`${data.name}${data.branch && data.branch !== 'Default' ? ` (${data.branch})` : ''}: ${player.stages.length} stage(s), ` +
      `${player.stages.reduce((n, s) => n + s.notifies.length, 0)} effect(s)${missing ? `, ${missing} animation(s) not found` : ''}.`);
    replay();
  }

  function replay() {
    restart();
    player.playing = true;
    animation.begin();
    render();
  }

  // Every effect the skill starts is made once up front, drawing nothing, and its shaders are compiled then, so the
  // first time an effect appears it does not stall the frame. They stay until playback stops so the compiled
  // programs are kept.
  function prewarm(data) {
    disposeWarm();
    const group = new THREE.Group();
    world.add(group);
    const systems = new Set(data.stages.flatMap((stage) => stage.notifies.map((notify) => notify.system)));
    const effects = [...systems].filter((system) => data.systems[system])
      .map((system) => new ParticleEffect({ model: data.systems[system], root: group, assets }));
    player.warm = { group, effects };
    if (renderer && camera) {
      const compiled = renderer.compileAsync ? renderer.compileAsync(world, camera) : Promise.resolve(renderer.compile(world, camera));
      compiled.then(() => requestRender()).catch(() => {});
    }
  }

  function disposeWarm() {
    if (!player.warm) return;
    player.warm.effects.forEach((effect) => effect.dispose());
    player.warm.group.removeFromParent();
    player.warm = null;
  }

  function restart() {
    for (const { effect, anchor } of player.effects) { effect.dispose(); anchor.removeFromParent(); }
    player.effects = [];
    player.fired = new Set();
    player.time = 0;
    player.last = 0;
  }

  function stop() {
    if (!player.playing) return;
    player.playing = false;
    restart();
    animation.end();
    requestRender();
    render();
  }

  function stageAt(time) {
    for (let index = player.stages.length - 1; index >= 0; index--) if (time >= player.stages[index].begins) return index;
    return 0;
  }

  function tick(now) {
    requestAnimationFrame(tick);
    if (!player.playing || !player.data) return;
    const dt = player.last ? Math.min(0.05, (now - player.last) / 1000) * player.speed : 0;
    player.last = now;
    player.time += dt;
    const t = player.time;
    if (t > player.length) {
      // After the last stage the character holds its final pose while the effects play out.
      const last = [...player.stages].reverse().find((stage) => stage.anim && player.clips.has(stage.anim));
      const clip = last ? player.clips.get(last.anim) : null;
      if (clip) animation.pose(clip, Math.max(0, (clip.frames - 1.01) / clip.rate));
    } else {
      const index = stageAt(t);
      const stage = player.stages[index];
      const local = t - stage.begins;
      const clip = stage.anim ? player.clips.get(stage.anim) : null;
      if (clip) animation.pose(clip, Math.min(local * (stage.rate || 1), Math.max(0, (clip.frames - 1.01) / clip.rate)));
      // Effects due in this stage (and any stage skipped over in a long frame).
      for (let s = 0; s <= index; s++) {
        player.stages[s].notifies.forEach((notify, n) => {
          const key = `${s}:${n}`;
          if (player.fired.has(key) || t < player.stages[s].begins + notify.start) return;
          player.fired.add(key);
          if (!plays(notify)) return;
          const model = player.data.systems[notify.system];
          const anchor = model ? attachment(notify) : null;
          if (!model || !anchor) return;
          const effect = new ParticleEffect({ model, root: world, parent: anchor, assets, parameters: notify.parameters, ground: ground?.() ?? 0 });
          player.effects.push({ effect, anchor, stopAt: notify.duration > 0 ? t + notify.duration : Infinity, startedAt: t });
        });
      }
    }
    for (const item of player.effects) {
      if (item.effect.active && t >= item.stopAt) item.effect.deactivate();
      item.effect.update(dt);
      item.effect.write();
    }
    // A game shader the browser could not compile falls back to the approximation.
    if (renderer && ++player.frames % 30 === 1) assets.checkPrograms(renderer, [...player.effects.map((item) => item.effect), ...(player.warm?.effects ?? [])]);
    // Effects that are done: stopped or past their emitters' end, with no particles left.
    player.effects = player.effects.filter((item) => {
      const done = (!item.effect.active || item.effect.time > item.effect.finishedAt) && !item.effect.alive && item.effect.time > 0.1;
      if (done) { item.effect.dispose(); item.anchor.removeFromParent(); }
      return !done;
    });
    if (t > player.length + TAIL) {
      if (player.loop) restart();
      else stop();
    }
    requestRender();
  }
  requestAnimationFrame(tick);

  return {
    handle(message) {
      if (message?.type === 'skills') {
        ui.offer = message;
        ui.loadingOffer = false;
        ui.classFile = message.defaultClass;
        tab.hidden = false;
        render();
      } else if (message?.type === 'skillsLoading') {
        // A player body: the tab shows at once and fills in when the game's skill list has been read.
        ui.loadingOffer = true;
        tab.hidden = false;
        render();
      } else if (message?.type === 'skillsUnavailable') {
        ui.loadingOffer = false;
        tab.hidden = true;
        render();
      } else if (message?.type === 'skillStatus') progress(message.text, message.progress ?? null);
      else if (message?.type === 'skill') start(message).catch((error) => status(`Could not play it: ${error.message}`));
      else if (message?.type === 'load' && !message.keepView) {
        stop();
        disposeWarm();
        player.data = null;
        ui.offer = null;
        ui.loadingOffer = false;
        tab.hidden = true;
        render();
      }
    },
  };
}
