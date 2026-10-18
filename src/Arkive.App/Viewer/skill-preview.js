// The model viewer's skill preview: the shown character plays one of its class's skills as the game's action data
// describes it. Each stage plays its animation; each effect the skill starts appears at its time, follows its socket
// (or bone) and takes the colour, size and other values the skill hands it.
import * as THREE from 'three';
import { ParticleEffect, EffectAssets } from './particle-sim.js';

const escapeHtml = (text) => String(text ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

// How long effects may play on after the last stage, so their particles can finish.
const TAIL = 2.5;

export function createSkillPreview(context) {
  const { world, $, post, requestRender, model, scene, socketOffset, animation } = context;
  const tab = document.querySelector('[data-tab="skills"]');
  const panel = $('tab-skills');
  const ui = { offer: null, classFile: '', skill: 0, branch: 0 };
  const assets = new EffectAssets();
  const player = { data: null, clips: new Map(), time: 0, playing: false, speed: 1, loop: true, effects: [], fired: new Set(), last: 0, stages: [] };
  window.arkiveSkills = { ui, player };

  function render() {
    const offer = ui.offer;
    if (!offer) { panel.innerHTML = ''; return; }
    const classes = offer.classes;
    const current = classes.find((item) => item.file === ui.classFile) ?? classes[0];
    const skill = current?.skills.find((item) => item.id === ui.skill) ?? current?.skills[0];
    ui.classFile = current?.file ?? '';
    ui.skill = skill?.id ?? 0;
    ui.branch = Math.min(ui.branch, Math.max(0, (skill?.branches.length ?? 1) - 1));
    panel.innerHTML = `
      <p class="muted">Plays a skill as the game's own skill data describes it: its animations, and each effect at its time and
        socket with the colours and sizes the skill gives it. Effects are approximations of the game's renderer.</p>
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
      <div id="skillStatus" class="muted"></div>
      <div id="skillTimeline" class="mono"></div>`;
    $('skillClass').addEventListener('change', (e) => { ui.classFile = e.target.value; ui.skill = 0; ui.branch = 0; render(); });
    $('skillSelect').addEventListener('change', (e) => { ui.skill = Number(e.target.value); ui.branch = 0; render(); });
    $('skillBranch')?.addEventListener('change', (e) => { ui.branch = Number(e.target.value); });
    $('skillSpeed').addEventListener('change', (e) => { player.speed = Number(e.target.value); });
    $('skillLoop').addEventListener('change', (e) => { player.loop = e.target.checked; });
    $('skillPlay').addEventListener('click', () => {
      if (player.playing) { stop(); return; }
      status('Preparing the skill…');
      post({ type: 'playSkill', classFile: ui.classFile, skill: ui.skill, branch: ui.branch });
    });
    renderTimeline();
  }

  function status(text) { const element = $('skillStatus'); if (element) element.textContent = text; }

  function renderTimeline() {
    const element = $('skillTimeline');
    if (!element || !player.data) return;
    element.innerHTML = player.stages.map((stage, index) => `<div><b>${index + 1}. ${escapeHtml(stage.anim ?? 'no animation')}</b>
      ${stage.duration.toFixed(2)} s</div>` + stage.notifies.map((notify) =>
      `<div>&nbsp;&nbsp;${notify.start.toFixed(2)} s · ${escapeHtml(notify.system.split('.').pop())}${notify.socket ? ` @ ${escapeHtml(notify.socket)}` : ''}</div>`).join('')).join('');
  }

  // ---- Playback ----
  function bonesByName() {
    const bones = new Map();
    model()?.traverse((object) => { if (object.isBone) bones.set(object.name.replace(/__ARKIVE_\d+$/, '').toLowerCase(), object); });
    return bones;
  }

  // The object an effect follows: the named socket, else a bone of that name, else the character itself. Its scale
  // turns the effect's Unreal centimetres into the viewer's units.
  function attachment(socketName) {
    const root = model();
    if (!root) return null;
    const bones = bonesByName();
    let parent = root, offset = new THREE.Vector3();
    if (socketName) {
      const socket = scene()?.sockets?.find((item) => item.name.toLowerCase() === socketName.toLowerCase());
      const bone = socket ? bones.get(socket.bone.toLowerCase()) : bones.get(socketName.toLowerCase());
      if (bone) { parent = bone; if (socket) offset = socketOffset(socket); }
    }
    const object = new THREE.Object3D();
    object.position.copy(offset);
    parent.add(object);
    parent.updateWorldMatrix(true, false);
    const scale = new THREE.Vector3().setFromMatrixScale(parent.matrixWorld).x || 1;
    object.scale.setScalar(0.01 / scale);
    return object;
  }

  async function start(data) {
    stop();
    player.data = data;
    assets.add({ textures: data.assets?.items, meshes: data.assets?.meshes });
    let at = 0;
    player.stages = data.stages.map((stage) => {
      const begins = at;
      at += Math.max(0.05, stage.duration);
      return { ...stage, begins };
    });
    player.length = at;
    renderTimeline();
    status(`Loading ${data.name}'s animations…`);
    player.clips.clear();
    const list = data.animations.list;
    for (const stage of player.stages) {
      if (!stage.anim || player.clips.has(stage.anim)) continue;
      const info = list.find((item) => item.name.toLowerCase() === stage.anim.toLowerCase());
      if (info) player.clips.set(stage.anim, await animation.load(data.animations.baseUrl, data.animations.bones, info));
    }
    const missing = player.stages.filter((stage) => stage.anim && !player.clips.has(stage.anim)).length;
    status(`${data.name}${data.branch && data.branch !== 'Default' ? ` (${data.branch})` : ''}: ${player.stages.length} stage(s), ` +
      `${player.stages.reduce((n, s) => n + s.notifies.length, 0)} effect(s)${missing ? `, ${missing} animation(s) not found` : ''}.`);
    restart();
    player.playing = true;
    animation.begin();
    render();
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
    if (t <= player.length) {
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
          const model = player.data.systems[notify.system];
          const anchor = model ? attachment(notify.socket) : null;
          if (!model || !anchor) return;
          const effect = new ParticleEffect({ model, root: world, parent: anchor, assets, parameters: notify.parameters });
          player.effects.push({ effect, anchor, stopAt: notify.duration > 0 ? t + notify.duration : Infinity, startedAt: t });
        });
      }
    }
    for (const item of player.effects) {
      if (item.effect.active && t >= item.stopAt) item.effect.deactivate();
      item.effect.update(dt);
      item.effect.write();
    }
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
        ui.classFile = message.defaultClass;
        tab.hidden = false;
        render();
      } else if (message?.type === 'skillStatus') status(message.text);
      else if (message?.type === 'skill') start(message).catch((error) => status(`Could not play it: ${error.message}`));
      else if (message?.type === 'load' && !message.keepView) {
        stop();
        ui.offer = null;
        tab.hidden = true;
        render();
      }
    },
  };
}
