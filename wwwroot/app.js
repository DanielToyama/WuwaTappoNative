/* Wuwatappo 主面板：本地 HTML 与原生层通过 postMessage 通信。
   所有接口都由 C# 代发，页面不接触 token。 */
(() => {
  const ICONS = {
    bag: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 8h12l1.5 12H4.5L6 8Z"/><path d="M9 8V6a3 3 0 0 1 6 0v2"/><path d="M9.5 13h5"/></svg>',
    atlas: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21.5Z"/><path d="M4 5.5v16"/><path d="M8 8h8M8 12h6"/></svg>',
    synth: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3h6"/><path d="M10 3v5l-5 9.5A2.5 2.5 0 0 0 7.2 21h9.6a2.5 2.5 0 0 0 2.2-3.5L14 8V3"/><path d="M7.5 15h9"/></svg>',
    preset: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8-5.2-2.7-5.2 2.7 1-5.8L3.5 9.7l5.9-.9L12 3.5Z"/></svg>',
    room: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="9" cy="8" r="3.2"/><path d="M3.5 20a5.5 5.5 0 0 1 11 0"/><circle cx="17.5" cy="9.5" r="2.4"/><path d="M15 20a4.5 4.5 0 0 1 6 0"/></svg>',
    settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3.2"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06A1.7 1.7 0 0 0 15 19.4a1.7 1.7 0 0 0-1 1.55V21a2 2 0 1 1-4 0v-.09A1.7 1.7 0 0 0 9 19.4a1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.55-1H3a2 2 0 1 1 0-4h.09A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.7 1.7 0 0 0 9 4.6h.09A1.7 1.7 0 0 0 10.1 3.05V3a2 2 0 1 1 4 0v.09A1.7 1.7 0 0 0 15 4.6a1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.7 1.7 0 0 0 19.4 9v.09A1.7 1.7 0 0 0 20.95 10.1H21a2 2 0 1 1 0 4h-.09A1.7 1.7 0 0 0 19.4 15Z"/></svg>'
  };

  const PAGES = [
    { key: 'bag', title: '我的背包', icon: 'bag' },
    { key: 'atlas', title: '图鉴', icon: 'atlas' },
    { key: 'synth', title: '合成', icon: 'synth' },
    { key: 'preset', title: '我的预设', icon: 'preset' },
    { key: 'room', title: '联机', icon: 'room' },
    { key: 'settings', title: '设置', icon: 'settings' }
  ];

  const RARITY_TEXT = { epic: '史诗', rare: '稀有', uncommon: '优良', common: '普通' };
  const RARITY_ORDER = { epic: 4, rare: 3, uncommon: 2, common: 1 };
  const TYPE_TEXT = { character_skin: '角色', accessory: '配件', emote: '表情' };

  const state = {
    boot: null,
    page: 'bag',
    bag: null,
    bagTab: 'recent',
    bagSeries: '',
    bagRole: '',
    bagRarity: '',
    bagHidden: new Set(),
    bagSearch: '',
    bagSelected: null,
    bagNewIds: [],
    bagRecentIds: [],
    disclaimerOpen: false,
    uacPromptOpen: false,
    favors: new Set(),
    catalog: {},
    atlas: {},
    atlasType: 'all',
    atlasSeries: '',
    atlasRole: '',
    atlasRarity: '',
    atlasHidden: new Set(),
    atlasSelected: null,
    synthMode: 'up',
    synthType: 'character_skin',
    synthRarity: 'all',
    synthSlots: [],
    presets: null,
    presetBusy: false,
    emoteNames: {},
    emoteCatalog: null
  };

  /* ---------------- Bridge ---------------- */
  let seq = 1;
  const pending = new Map();

  function call(cmd, data = {}) {
    return new Promise((resolve, reject) => {
      const id = seq++;
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, cmd, data });
    });
  }

  window.chrome.webview.addEventListener('message', ev => {
    let msg = ev.data;
    if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch { return; } }
    if (!msg) return;
    if (msg.evt) { handleEvent(msg.evt, msg.data); return; }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.data);
    else p.reject(new Error(msg.error || '操作失败'));
  });

  async function tryCall(cmd, data) {
    try { return await call(cmd, data); }
    catch (e) { toast(e.message || String(e), 'err'); throw e; }
  }

  window.addEventListener('error', e => {
    try { toast('脚本错误：' + (e.message || '未知'), 'err'); } catch { }
  });
  window.addEventListener('unhandledrejection', e => {
    try { toast('脚本异常：' + (e.reason?.message || e.reason || '未知'), 'err'); } catch { }
  });

  /* ---------------- 基础 UI ---------------- */
  function toast(text, kind = 'info') {
    const host = document.getElementById('toastHost');
    const el = document.createElement('div');
    el.className = 'toast' + (kind === 'err' ? ' err' : '');
    el.textContent = text;
    host.appendChild(el);
    setTimeout(() => { el.style.opacity = '0'; el.style.transition = 'opacity .25s'; }, 2200);
    setTimeout(() => el.remove(), 2600);
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function modal(html, onBind) {
    const host = document.getElementById('modalHost');
    host.innerHTML = `<div class="modal">${html}</div>`;
    host.classList.remove('hidden');
    host.onclick = e => { if (e.target === host) closeModal(); };
    onBind && onBind(host);
  }
  function closeModal() {
    const host = document.getElementById('modalHost');
    host.classList.add('hidden');
    host.innerHTML = '';
  }

  function rarityClass(r) { return 'rarity-' + (r || 'common'); }
  function effRarity(r) { return (r || 'common').toLowerCase(); }

  function img(url, cls = '', alt = '') {
    if (!url) return `<div class="${cls}"></div>`;
    return `<img class="${cls}" loading="lazy" src="${esc(url)}" alt="${esc(alt)}" onerror="this.style.visibility='hidden'">`;
  }

  const DEMO = new URLSearchParams(location.search).has('demo');

  function isLogged() { return DEMO || !!(state.boot && state.boot.auth && state.boot.auth.loggedIn); }

  /* ---------------- 初始化 ---------------- */
  const HASH_PARAMS = new URLSearchParams(location.hash.replace(/^#/, ''));

  function init() {
    const hashParams = HASH_PARAMS;
    const initialPage = hashParams.get('page');
    if (initialPage && PAGES.some(p => p.key === initialPage)) state.page = initialPage;
    document.getElementById('tabs').innerHTML = PAGES.map(p =>
      `<button class="tab" data-page="${p.key}">${ICONS[p.icon]}<span>${p.title}</span></button>`).join('');
    document.querySelectorAll('.tab').forEach(btn => btn.onclick = () => navigate(btn.dataset.page));
    document.getElementById('closeBtn').onclick = () => call('win.close');
    document.getElementById('winCloseBtn').onclick = () => call('win.close');
    document.getElementById('minBtn').onclick = () => call('win.min');
    document.getElementById('titlebar').addEventListener('mousedown', e => {
      if (e.target.closest('button')) return;
      call('win.drag');
    });
    navigate(state.page);
    setInterval(updateChestStatus, 1000);
  }

  function maybeOpenEmoteEditor() {
    if (HASH_PARAMS.get('editEmotes') === '1' && isLogged() && !state.editEmotesOpened) {
      state.editEmotesOpened = true;
      setTimeout(() => openEmoteEditor(), 400);
    }
  }

  async function boot() {
    try {
      const b = await call('boot');
      state.boot = b;
      renderChrome();
      renderPage();
      maybeShowDisclaimer();
      maybeShowUacPrompt();
      maybeOpenEmoteEditor();
    } catch (e) { toast('初始化失败：' + e.message, 'err'); }
  }

  const DISCLAIMER_TEXT = [
    '如果你花费了任何金额购买了此程序，那你绝对是被骗了，请你立刻退款并差评。',
    '本程序所有素材版权属于库洛游戏所有，本项目与库洛无任何关联，如有侵权，请立刻联系我。',
    '本程序只用于对新技术的学习与研究，任何下载了此项目的用户请在24h内删除本程序，谢谢配合。'
  ];

  function maybeShowDisclaimer() {
    if (!state.boot || state.boot.disclaimerShown) return;
    if (state.disclaimerOpen) return;
    state.disclaimerOpen = true;
    modal(`
      <div class="about">
        <div class="about-title">免责声明</div>
        <div class="about-note about-note--warn">
          ${DISCLAIMER_TEXT.map(t => `<p>${esc(t)}</p>`).join('')}
        </div>
        <button class="btn" id="mAgree" style="align-self:center;min-width:160px">我已知晓</button>
      </div>`, host => {
      // 首次启动的声明必须点按钮确认，点遮罩不关闭。
      host.onclick = null;
      host.querySelector('#mAgree').onclick = async () => {
        state.disclaimerOpen = false;
        if (state.boot) state.boot.disclaimerShown = true;
        closeModal();
        try { await call('ui.disclaimerShown'); } catch { }
        // 免责声明之后再询问一次 UAC；使用 setTimeout 让当前弹窗先完成关闭。
        setTimeout(() => maybeShowUacPrompt(), 120);
      };
    });
  }

  function maybeShowUacPrompt() {
    if (!state.boot || state.boot.uacPromptShown) return;
    if (state.boot.settings?.isElevated) return;
    if (state.uacPromptOpen || state.disclaimerOpen) return;
    state.uacPromptOpen = true;
    modal(`
      <div class="about">
        <div class="about-title">管理员权限（UAC）</div>
        <div class="about-note">
          <p>是否授予该应用管理员 UAC 权限？</p>
          <p>标准权限无法监听其他 UAC 程序的输入。本程序只把管理员权限用于触发键位，不会有任何敏感操作、上传键盘内容等行为；可自行使用 AI 等工具审查源代码。</p>
          <p>如不授予，可能会影响部分程序内的键位统计，其他功能不受影响。</p>
        </div>
        <div style="display:flex;gap:10px;justify-content:center">
          <button class="btn" id="mUacYes">是</button>
          <button class="btn ghost" id="mUacNo">否</button>
        </div>
      </div>`, host => {
      host.onclick = null;
      const yes = host.querySelector('#mUacYes');
      const no = host.querySelector('#mUacNo');
      const finish = (granted) => {
        yes.disabled = true; no.disabled = true;
        state.uacPromptOpen = false;
        if (state.boot) state.boot.uacPromptShown = true;
        closeModal();
        try { call('ui.uacPromptResult', { granted }); } catch { }
      };
      yes.onclick = async () => {
        yes.disabled = true; no.disabled = true;
        yes.textContent = '正在请求…';
        try {
          const r = await call('ui.uacPromptResult', { granted: true });
          state.uacPromptOpen = false;
          if (state.boot) {
            state.boot.uacPromptShown = true;
            if (state.boot.settings) state.boot.settings.runAsAdmin = !!r?.granted;
          }
          closeModal();
          if (r?.restarting) toast('已授予管理员权限，正在重启…');
          else if (r?.cancelled) toast('已取消 UAC，继续使用普通权限；可随时在设置中开启', 'err');
          else if (r?.elevated) toast('已启用管理员权限启动');
        } catch {
          yes.disabled = false; no.disabled = false;
          yes.textContent = '是';
          toast('请求管理员权限失败', 'err');
        }
      };
      no.onclick = () => {
        if (state.boot?.settings) state.boot.settings.runAsAdmin = false;
        finish(false);
        toast('已跳过管理员权限；可随时在设置中开启');
      };
    });
  }

  function showAbout() {
    const version = state.boot?.version || '0.0.1';
    const link = (href, text) => `<a class="about-link" href="${esc(href)}" target="_blank" rel="noreferrer">${esc(text)}</a>`;
    modal(`
      <div class="about">
        <div class="about-head">
          <div class="about-badge">W</div>
          <div>
            <div class="about-name">Wuwatappo Native</div>
            <div class="about-ver">版本 ${esc(version)}</div>
          </div>
        </div>
        <div class="about-note">
          ${DISCLAIMER_TEXT.map(t => `<p>${esc(t)}</p>`).join('')}
        </div>
        <div class="about-sec">
          <div class="about-sec-title">作者</div>
          <p>本程序由 ${link('http://danieluu.top', 'Daniel_兔兔')} 参考官方 WuwaTappo 制作。</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">特别鸣谢</div>
          <p>${link('https://download-mc-wuwatappo.kurogames-global.com/download/', '库洛游戏')}</p>
          <p>${link('https://github.com/Fantasicrap/', '@Fantasicrap')}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">项目地址</div>
          <p>GitHub：${link('https://github.com/DanielToyama/WuwaTappoNative', 'DanielToyama/WuwaTappoNative')}</p>
        </div>
        <button class="btn ghost" id="mCloseAbout" style="align-self:center;min-width:120px">关闭</button>
      </div>`, host => {
      host.querySelector('#mCloseAbout').onclick = closeModal;
    });
  }

  function showInheritLogin() {
    modal(`
      <div class="about">
        <div class="about-title">继承官方程序登录状态</div>
        <div class="about-note about-note--warn">
          <p>建议在获取前先关闭本家程序的开机自动启动，以免下次开机因为 token 冲突导致掉线。</p>
          <p>继续后会读取本机官方 WuwaTappo 客户端的登录数据；如果官方客户端正在运行，会被关闭。只读取本机文件，不上传 token。</p>
          <p>如果当前已经登录了其他账号，会被官方客户端的账号替换。请确认官方客户端已经在当前 Windows 用户下登录过一次。</p>
        </div>
        <div style="display:flex;gap:10px;justify-content:center">
          <button class="btn ghost" id="inheritCancel">取消</button>
          <button class="btn" id="inheritGo">开始继承</button>
        </div>
      </div>`, host => {
      const cancel = host.querySelector('#inheritCancel');
      const go = host.querySelector('#inheritGo');
      let busy = false;
      host.onclick = e => { if (e.target === host && !busy) closeModal(); };
      cancel.onclick = () => { if (!busy) closeModal(); };
      go.onclick = async () => {
        if (busy) return;
        busy = true;
        cancel.disabled = true;
        go.disabled = true;
        go.textContent = '正在读取…';
        try {
          const r = await tryCall('token.inherit');
          state.boot = await call('boot');
          closeModal();
          renderChrome();
          navigate('bag');
          toast('已继承官方程序登录状态' + (r?.nickname ? '：' + r.nickname : ''));
        } catch {
          busy = false;
          cancel.disabled = false;
          go.disabled = false;
          go.textContent = '开始继承';
        }
      };
    });
  }

  function handleEvent(evt, data) {
    if (evt === 'boot') { state.boot = data; renderChrome(); renderPage(); maybeShowDisclaimer(); maybeShowUacPrompt(); maybeOpenEmoteEditor(); return; }
    if (evt === 'auth') { if (state.boot) state.boot.auth = { ...state.boot.auth, ...data }; renderChrome(); renderPage(); return; }
    if (evt === 'room') {
      if (!state.boot) return;
      state.boot.room = data;
      renderChrome();
      if (state.page !== 'room') return;
      // 房间推送可能成批到达；内容没变就不重建整页（人数多时 DOM 重建很贵）。
      const sig = roomSignature(data);
      if (sig !== state.roomSig) { state.roomSig = sig; renderRoom(); }
      return;
    }
    if (evt === 'toast') { toast(data.text, data.kind); return; }
    // 自动领宝箱发生在后台，面板收到后同步背包与「最近获得」。
    if (evt === 'chest') { refreshAfterAcquire(data?.asset_id); return; }
    if (evt === 'clicks') {
      applyClicks(typeof data === 'object' && data !== null ? data : { available: data });
      return;
    }
  }

  // 点击数每按一次键都会变：只改对应文字节点，不整页重绘（房间里人多时重绘代价很高）。
  function applyClicks(v) {
    if (!state.boot?.auth) return;
    if (v.available != null) state.boot.auth.clicks = v.available;
    if (v.total != null) state.boot.auth.clickTotal = v.total;
    if (v.consume != null) state.boot.auth.clickConsume = v.consume;
    const setText = (sel, val) => {
      if (val == null) return;
      document.querySelectorAll(sel).forEach(el => { if (el.textContent !== String(val)) el.textContent = val; });
    };
    setText('[data-click-avail]', v.available);
    setText('[data-click-total]', v.total);
    setText('[data-click-consume]', v.consume);
  }

  function renderChrome() {
    const auth = state.boot?.auth;
    // 左下角只显示昵称，不再暴露 UID。
    document.getElementById('userChip').textContent = auth?.loggedIn
      ? (auth.nickname || '漂泊者')
      : '未登录';
    document.querySelectorAll('.tab').forEach(btn => btn.classList.toggle('active', btn.dataset.page === state.page));
    const page = PAGES.find(p => p.key === state.page);
    document.getElementById('pageTitle').textContent = page ? page.title : '';
  }

  function navigate(key) {
    state.page = key;
    renderChrome();
    renderPage();
  }

  function renderPage() {
    const host = document.getElementById('pageHost');
    if (!isLogged() && state.page !== 'settings') {
      host.innerHTML = renderLogin();
      bindLogin();
      return;
    }
    if (state.page === 'bag') renderBag();
    else if (state.page === 'atlas') renderAtlas();
    else if (state.page === 'synth') renderSynth();
    else if (state.page === 'preset') renderPreset();
    else if (state.page === 'room') renderRoom();
    else renderSettings();
  }

  function setPage(html) { document.getElementById('pageHost').innerHTML = `<div class="page">${html}</div>`; }

  /* ---------------- 登录 ---------------- */
  function renderLogin() {
    return `<div class="login-wrap"><div class="login-card">
      <h2>登录 Wuwa Tappo</h2>
      <div class="hint">使用游戏 UID 登录；如果无法完成验证码登录，请使用官方登录状态。</div>
      <input class="input" id="loginUid" placeholder="游戏 UID（9-10 位）" inputmode="numeric" maxlength="10" />
      <div style="display:flex;gap:10px">
        <input class="input" id="loginCode" placeholder="6 位验证码" style="flex:1" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
        <button class="btn ghost" id="sendCodeBtn">发送验证码</button>
      </div>
      <button class="btn" id="loginBtn">登录</button>
      <div class="hint">如果无法完成 UID 验证码登录，请先在官方客户端登录，再点击下方按钮继承官方登录状态。</div>
      <button class="btn ghost" id="loginInherit">继承官方程序登录状态</button>
    </div></div>`;
  }

  function bindLogin() {
    const send = document.getElementById('sendCodeBtn');
    const go = document.getElementById('loginBtn');
    const uidEl = document.getElementById('loginUid');
    const codeEl = document.getElementById('loginCode');
    // 原版只允许输入数字，这里保持一致，避免把非数字发给服务端。
    const digitsOnly = el => el.addEventListener('input', () => {
      const v = el.value.replace(/[^0-9]/g, '');
      if (el.value !== v) el.value = v;
    });
    digitsOnly(uidEl); digitsOnly(codeEl);

    // 手机号登录已移除，只接受 9-10 位游戏 UID。
    const uidValid = v => /^[0-9]{9,10}$/.test(v);
    send.onclick = async () => {
      const uid = uidEl.value.trim();
      if (!uid) return toast('请输入游戏 UID', 'err');
      if (!uidValid(uid)) return toast('请输入 9-10 位游戏 UID', 'err');
      try { await tryCall('login.sendCode', { uid }); toast('验证码已发送'); } catch { }
    };
    go.onclick = async () => {
      const uid = uidEl.value.trim();
      const code = codeEl.value.trim();
      if (!uid) return toast('请输入游戏 UID', 'err');
      if (!uidValid(uid)) return toast('请输入 9-10 位游戏 UID', 'err');
      if (!/^[0-9]{6}$/.test(code)) return toast('请输入 6 位数字验证码', 'err');
      try {
        await tryCall('login.submit', { uid, code });
        toast('登录成功');
        state.boot = await call('boot');
        renderChrome();
        navigate('bag');
      } catch { }
    };
    document.getElementById('loginInherit')?.addEventListener('click', showInheritLogin);
  }

  /* ---------------- 背包 ---------------- */
  async function ensureBag(force = false) {
    if (state.bag && !force) return state.bag;
    const r = await tryCall('bag.list');
    state.bag = r.items || [];
    return state.bag;
  }

  const BAG_TABS = [['recent', '最近获得'], ['character_skin', '角色'], ['accessory', '配件'], ['emote', '表情']];
  const BAG_TAB_COUNT = k => k === 'recent' ? (state.bagRecentIds || []).length : 0;

  function assetMeta(item) {
    const name = String(item.assetName || '').replace(/\.png$/i, '');
    let m;
    if (item.type === 'character_skin' && (m = name.match(/^role_(\d+)_(\d+)$/i))) return { role: m[1], series: item.series || m[2] };
    if (item.type === 'emote' && (m = name.match(/^emote_(\d+)_/i))) return { role: m[1], series: '' };
    return { role: '0', series: item.series || '' };
  }
  function roleLabelOf(item) {
    const first = String(item.name || '').split('・')[0];
    return first || (assetMeta(item).role === '0' ? '通用' : '角色 ' + item.roleId);
  }
  const RECENT_KEY = 'wn_recent_assets';
  const UNSEEN_KEY = 'wn_unseen_assets';
  const RECENT_MAX = 80;

  /**
   * 「最近获得」是一个常驻的有序列表：新获得的物品逐个插到最前面，旧的一直往后排。
   * 与「已认识」集合分开保存，所以切换页签、重启都不会丢，也不会整页清空。
   */
  function loadRecent() {
    try {
      const raw = localStorage.getItem(RECENT_KEY);
      if (raw == null) {
        // 旧版本只存了「本次新增」：升级时把它迁成常驻列表，用户不会看到空白的最近获得。
        const legacy = JSON.parse(localStorage.getItem('wn_new_assets') || '[]');
        const seeded = Array.isArray(legacy) ? legacy.filter(x => typeof x === 'number') : [];
        if (seeded.length) saveRecent(seeded);
        return seeded;
      }
      const arr = JSON.parse(raw);
      return Array.isArray(arr) ? arr.filter(x => typeof x === 'number') : [];
    } catch { return []; }
  }
  function saveRecent(list) {
    try { localStorage.setItem(RECENT_KEY, JSON.stringify(list.slice(0, RECENT_MAX))); } catch { }
  }
  function loadUnseen() {
    try {
      const arr = JSON.parse(localStorage.getItem(UNSEEN_KEY) || '[]');
      return Array.isArray(arr) ? arr.filter(x => typeof x === 'number') : [];
    } catch { return []; }
  }
  function saveUnseen(list) {
    try { localStorage.setItem(UNSEEN_KEY, JSON.stringify([...new Set(list)])); } catch { }
  }
  /** 宝箱响应只有 id；先把它前插到最近获得并标记小红点，不等下一次背包扫描。 */
  function markRecentObtained(id) {
    id = Number(id);
    if (!Number.isFinite(id) || id <= 0) return;
    const recent = [id, ...loadRecent().filter(x => x !== id)];
    saveRecent(recent);
    const unseen = [id, ...loadUnseen().filter(x => x !== id)];
    saveUnseen(unseen);
    const owned = new Set((state.bag || []).map(x => x.id));
    state.bagRecentIds = recent.filter(x => owned.has(x));
    state.bagNewIds = unseen.filter(x => owned.has(x));
  }
  /** 点击/查看后清除该物品的小红点，直到它再次被重新获取。 */
  function markNewSeen(id) {
    const unseen = loadUnseen().filter(x => x !== id);
    saveUnseen(unseen);
    state.bagNewIds = (state.bagNewIds || []).filter(x => x !== id);
  }
  function refreshNewAssets() {
    try {
      const rawKnown = localStorage.getItem('wn_known_assets');
      const known = new Set(JSON.parse(rawKnown || '[]'));
      const ids = state.bag.map(x => x.id);
      const firstRun = rawKnown == null;
      // 首次运行不把整包物品都标成“最近获得”，只把已知集合建立起来。
      const fresh = firstRun ? [] : ids.filter(id => !known.has(id));
      let recent = loadRecent();
      if (fresh.length) {
        // 逐个前插：每发现一个新物品就放到第 1 位，已有的旧记录顺次后移。
        for (const id of fresh) {
          recent = [id, ...recent.filter(x => x !== id)];
        }
        saveRecent(recent);
      }
      // 小红点单独持久化，面板关闭/重开后仍然可见，直到用户点开该物品。
      const unseen = [...new Set([...fresh, ...loadUnseen()])];
      saveUnseen(unseen);
      // 背包里已不存在的（例如被消耗掉）不再显示，但保留记录以免误删历史。
      const owned = new Set(ids);
      state.bagRecentIds = recent.filter(id => owned.has(id));
      state.bagNewIds = unseen.filter(id => owned.has(id));
      localStorage.setItem('wn_known_assets', JSON.stringify([...new Set([...known, ...ids])]));
    } catch { state.bagRecentIds = []; state.bagNewIds = []; }
  }
  /** 保留记录：只在用户主动清空时调用。 */
  function dismissNewAssets() {
    saveRecent([]);
    saveUnseen([]);
    state.bagRecentIds = [];
    state.bagNewIds = [];
  }

  async function renderBag() {
    setPage(`<div class="loading">正在读取背包…</div>`);
    try { await ensureBag(); } catch { setPage(`<div class="empty-state">背包读取失败</div>`); return; }
    if (!state.bagNewReady) { refreshNewAssets(); state.bagNewReady = true; }

    const counts = { character_skin: 0, accessory: 0, emote: 0 };
    state.bag.forEach(x => { if (counts[x.type] != null) counts[x.type]++; });
    const tab = state.bagTab;
    const isChar = tab === 'character_skin';
    const showRole = tab === 'character_skin' || tab === 'emote';

    const seriesOptions = [...new Set(state.bag.filter(x => x.type === 'character_skin' && x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
    const roleMap = new Map();
    state.bag.filter(x => showRole).forEach(x => {
      const r = assetMeta(x).role;
      if (!roleMap.has(r)) roleMap.set(r, roleLabelOf(x));
    });
    const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));

    setPage(`
      <div class="bag-layout">
        <div class="bag-main">
          <div class="seg-tabs">
            ${BAG_TABS.map(([k, label]) => `<button class="seg-tab ${tab === k ? 'active' : ''}" data-btab="${k}">${label} ${k === 'recent' ? BAG_TAB_COUNT(k) : (counts[k] || 0)}</button>`).join('')}
          </div>
          <div class="filter-row">
            ${isChar ? `<select class="select" id="bagSeries"><option value="">系列：全部</option>${seriesOptions.map(s => `<option value="${esc(s)}" ${state.bagSeries === s ? 'selected' : ''}>系列：${esc(s)}</option>`).join('')}</select>` : ''}
            ${showRole ? `<select class="select" id="bagRole"><option value="">角色：全部</option>${roleOptions.map(([id, label]) => `<option value="${id}" ${state.bagRole === id ? 'selected' : ''}>角色：${esc(label)}</option>`).join('')}</select>` : ''}
            <select class="select" id="bagRarity">
              <option value="">稀有度：全部</option>
              ${['epic', 'rare', 'uncommon', 'common'].map(r => `<option value="${r}" ${state.bagRarity === r ? 'selected' : ''}>稀有度：${RARITY_TEXT[r]}</option>`).join('')}
            </select>
            <input class="input" id="bagSearch" placeholder="搜索名称 / 系列" value="${esc(state.bagSearch)}" style="flex:1;min-width:160px" />
            <button class="btn ghost" id="bagRefresh">刷新</button>
            ${tab === 'recent' ? '<button class="btn ghost" id="bagClearRecent">清空记录</button>' : ''}
            <button class="btn" id="bagSync">同步角色/配件</button>
          </div>
          <div class="bag-groups" id="bagGroups"></div>
        </div>
        <aside class="detail-panel" id="bagDetail"></aside>
      </div>`);

    document.querySelectorAll('[data-btab]').forEach(b => b.onclick = () => {
      state.bagTab = b.dataset.btab;
      state.bagSeries = ''; state.bagRole = ''; state.bagRarity = '';
      state.bagHidden = new Set();
      renderBag();
    });
    document.getElementById('bagSeries')?.addEventListener('change', e => { state.bagSeries = e.target.value; renderBagList(); });
    document.getElementById('bagRole')?.addEventListener('change', e => { state.bagRole = e.target.value; renderBagList(); });
    document.getElementById('bagRarity').addEventListener('change', e => { state.bagRarity = e.target.value; renderBagList(); });
    document.getElementById('bagSearch').oninput = e => { state.bagSearch = e.target.value; renderBagList(); };
    document.getElementById('bagRefresh').onclick = async () => { await ensureBag(true); state.bagNewReady = false; renderBag(); };
    document.getElementById('bagClearRecent')?.addEventListener('click', () => {
      dismissNewAssets();
      renderBag();
      toast('已清空最近获得记录');
    });
    document.getElementById('bagSync').onclick = async () => {
      try { await tryCall('appearance.sync'); toast('角色/配件已同步到云端（表情栏仅本地）'); } catch { }
    };
    renderBagList();
    renderBagDetail();
  }

  function bagListItems() {
    const q = String(state.bagSearch || '').trim().toLowerCase();
    let items;
    if (state.bagTab === 'recent') {
      const order = new Map((state.bagRecentIds || []).map((id, i) => [id, i]));
      items = state.bag.filter(x => order.has(x.id));
      if (state.bagRarity) items = items.filter(x => effRarity(x.rarity) === state.bagRarity);
      if (q) items = items.filter(x => (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
      // 最近获得按“最新的在最前”排，不再按稀有度打散。
      return items.sort((a, b) => (order.get(a.id) ?? 9999) - (order.get(b.id) ?? 9999));
    }
    items = state.bag.filter(x => x.type === state.bagTab);
    // 系列/角色下拉以前只改了 state，没有参与过滤，所以看起来“没反应”。
    if (state.bagSeries) items = items.filter(x => String(x.series || '') === state.bagSeries);
    if (state.bagRole) items = items.filter(x => assetMeta(x).role === state.bagRole);
    if (state.bagRarity) items = items.filter(x => effRarity(x.rarity) === state.bagRarity);
    if (q) items = items.filter(x => (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
    return items.sort((a, b) => (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
      || String(a.series).localeCompare(String(b.series), 'zh')
      || String(a.name).localeCompare(String(b.name), 'zh'));
  }

  function renderBagList() {
    const host = document.getElementById('bagGroups');
    if (!host) return;
    const items = bagListItems();
    if (!items.length) {
      host.innerHTML = `<div class="empty-state">没有符合条件的收集物</div>`;
      return;
    }
    if (state.bagTab === 'recent') {
      host.innerHTML = `<div class="bag-grid">${items.map(x => cardHtml(x, state.bagSelected?.id === x.id, true)).join('')}</div>`;
      bindBagCards(host);
      return;
    }
    const groups = new Map();
    for (const it of items) {
      const r = effRarity(it.rarity);
      if (!groups.has(r)) groups.set(r, []);
      groups.get(r).push(it);
    }
    const order = ['epic', 'rare', 'uncommon', 'common'];
    host.innerHTML = order.filter(r => groups.has(r)).map(r => {
      const list = groups.get(r);
      const hidden = state.bagHidden.has(r);
      return `<section class="bag-group">
        <div class="bag-group-head" data-bgroup="${r}"><span>${RARITY_TEXT[r]}</span><span class="count">${list.length}</span><span class="chev">${hidden ? '▸' : '▾'}</span></div>
        ${hidden ? '' : `<div class="bag-grid">${list.map(x => cardHtml(x, state.bagSelected?.id === x.id, true)).join('')}</div>`}
      </section>`;
    }).join('');
    host.querySelectorAll('[data-bgroup]').forEach(el => el.onclick = () => {
      const r = el.dataset.bgroup;
      if (state.bagHidden.has(r)) state.bagHidden.delete(r); else state.bagHidden.add(r);
      renderBagList();
    });
    bindBagCards(host);
  }

  function bindBagCards(host) {
    host.querySelectorAll('.card').forEach(el => {
      const id = Number(el.dataset.id);
      el.onclick = () => {
        markNewSeen(id);
        state.bagSelected = state.bag.find(v => v.id === id);
        renderBagList();
        renderBagDetail();
      };
      el.ondblclick = () => equip(itemOrId(id));
    });
    host.querySelectorAll('.fav').forEach(el => {
      el.onclick = async e => {
        e.stopPropagation();
        const id = Number(el.dataset.id);
        const on = !state.favors.has(id);
        try { await tryCall('favorite', { id, on }); on ? state.favors.add(id) : state.favors.delete(id); renderBagList(); }
        catch { }
      };
    });
  }

  function itemOrId(id) { return state.bag.find(v => v.id === id) || null; }

  function cardHtml(item, selected, withFav) {
    const fav = state.favors.has(item.id) ? '★' : '☆';
    const isNew = (state.bagNewIds || []).includes(item.id);
    return `<div class="card ${selected ? 'selected' : ''}" data-id="${item.id}">
      ${isNew ? '<span class="new-dot" title="最近获得"></span>' : ''}
      ${withFav ? `<div class="fav" data-id="${item.id}" title="收藏">${fav}</div>` : ''}
      ${img(item.thumb, 'thumb', item.name)}
      <div class="rarity-bar ${rarityClass(item.rarity)}"></div>
      <div class="name" title="${esc(item.name)}">${esc(item.name)}</div>
      <div class="sub">${esc(item.series || TYPE_TEXT[item.type] || '')}</div>
      ${item.quantity > 1 ? `<div class="qty">×${item.quantity}</div>` : ''}
    </div>`;
  }

  function renderBagDetail() {
    const box = document.getElementById('bagDetail');
    if (!box) return;
    const x = state.bagSelected;
    if (!x) {
      box.innerHTML = `<div class="detail-empty">选择一件收集物<br><span style="font-size:13px">双击可直接装备</span></div>`;
      return;
    }
    box.innerHTML = `
      <div class="detail-preview">${img(x.thumb, '', x.name)}</div>
      <div class="detail-name">${esc(x.name)}</div>
      <div class="detail-row"><span>类别</span><span>${TYPE_TEXT[x.type] || x.type}</span></div>
      <div class="detail-row"><span>稀有度</span><span>${RARITY_TEXT[effRarity(x.rarity)] || x.rarity}</span></div>
      <div class="detail-row"><span>系列</span><span>${esc(x.series || '—')}</span></div>
      <div class="detail-row"><span>数量</span><span>${x.quantity ?? 1}</span></div>
      <div class="spacer"></div>
      <button class="btn" id="equipBtn">装备（本地显示）</button>
      <button class="btn ghost" id="equipSyncBtn">装备并同步云端</button>`;
    document.getElementById('equipBtn').onclick = () => equip(x, false);
    document.getElementById('equipSyncBtn').onclick = () => equip(x, true);
  }

  async function equip(item, sync = false) {
    if (!item) return;
    try {
      await tryCall('appearance.set', { type: item.type, id: item.id });
      if (sync) await tryCall('appearance.sync');
      toast(sync ? '已装备并同步到云端' : '已装备到本地显示');
      state.boot = await call('boot');
      renderChrome();
    } catch { }
  }

  /* ---------------- 图鉴 ---------------- */
  async function ensureCatalog(type) {
    if (state.catalog[type]) return state.catalog[type];
    const r = await tryCall('catalog.list', { type });
    state.catalog[type] = r.items || [];
    return state.catalog[type];
  }

  async function ensureAtlas(type) {
    if (type === 'all') {
      const lists = await Promise.all(['character_skin', 'accessory', 'emote'].map(t => ensureAtlasOne(t).catch(() => [])));
      const all = lists.flat();
      if (!all.length) throw new Error('目录为空');
      return all;
    }
    return ensureAtlasOne(type);
  }

  async function ensureAtlasOne(type, retry = true) {
    if (state.atlas[type] && state.atlas[type].length) return state.atlas[type];
    try {
      const r = await tryCall('atlas.list', { type });
      const items = r.items || [];
      if (items.length) state.atlas[type] = items;
      return items;
    } catch (e) {
      if (!retry) throw e;
      await new Promise(res => setTimeout(res, 600));
      return ensureAtlasOne(type, false);
    }
  }

  const ATLAS_TABS = [['all', '全部'], ['character_skin', '角色'], ['accessory', '配件'], ['emote', '表情']];

  async function renderAtlas() {
    const type = state.atlasType;
    setPage(`<div class="loading">正在读取图鉴…</div>`);
    let items;
    try { items = await ensureAtlas(type); }
    catch {
      setPage(`<div class="empty-state">图鉴读取失败（网络波动）<br><button class="btn ghost" id="atlasRetry" style="margin-top:10px">重试</button></div>`);
      document.getElementById('atlasRetry').onclick = renderAtlas;
      return;
    }
    const ownedNames = new Set((state.bag || []).map(x => x.assetName));
    ensureBag().catch(() => { });
    ensureEmotes().catch(() => { });
    const showSeries = type === 'character_skin';
    const showRole = type !== 'accessory';

    const seriesOptions = [...new Set(items.filter(x => x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
    const roleMap = new Map();
    if (showRole) {
      for (const x of items) {
        const m = String(x.assetName || '').match(/^role_(\d+)_/i);
        if (!m) continue;
        if (!roleMap.has(m[1])) roleMap.set(m[1], String(x.name || '').split('・')[0] || m[1]);
      }
    }
    const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));

    setPage(`
      <div class="seg-tabs">
        ${ATLAS_TABS.map(([k, label]) => {
          const count = k === 'all' ? (items.length || '') : (state.atlas[k]?.length ?? '');
          return `<button class="seg-tab ${type === k ? 'active' : ''}" data-atab="${k}">${label} ${count}</button>`;
        }).join('')}
      </div>
      <div class="filter-row">
        ${showSeries ? `<select class="select" id="atlasSeries"><option value="">系列：全部</option>${seriesOptions.map(s => `<option value="${esc(s)}" ${state.atlasSeries === s ? 'selected' : ''}>系列：${esc(s)}</option>`).join('')}</select>` : ''}
        ${showRole ? `<select class="select" id="atlasRole"><option value="">角色：全部</option>${roleOptions.map(([id, label]) => `<option value="${id}" ${state.atlasRole === id ? 'selected' : ''}>角色：${esc(label)}</option>`).join('')}</select>` : ''}
        <select class="select" id="atlasRarity">
          <option value="">稀有度：全部</option>
          ${['epic', 'rare', 'uncommon', 'common'].map(r => `<option value="${r}" ${state.atlasRarity === r ? 'selected' : ''}>稀有度：${RARITY_TEXT[r]}</option>`).join('')}
        </select>
        <span class="spacer"></span>
        <span class="muted" style="font-size:13px">共 ${items.length} 件 · 已拥有会显示标记</span>
      </div>
      <div class="bag-groups" id="atlasGroups"></div>`);

    document.querySelectorAll('[data-atab]').forEach(b => b.onclick = () => {
      state.atlasType = b.dataset.atab;
      state.atlasSeries = ''; state.atlasRole = ''; state.atlasRarity = ''; state.atlasHidden = new Set();
      renderAtlas();
    });
    document.getElementById('atlasSeries')?.addEventListener('change', e => { state.atlasSeries = e.target.value; renderAtlasList(items, ownedNames); });
    document.getElementById('atlasRole')?.addEventListener('change', e => { state.atlasRole = e.target.value; renderAtlasList(items, ownedNames); });
    document.getElementById('atlasRarity').addEventListener('change', e => { state.atlasRarity = e.target.value; renderAtlasList(items, ownedNames); });
    renderAtlasList(items, ownedNames);
  }

  function atlasFilteredItems(items) {
    let list = items;
    if (state.atlasSeries) list = list.filter(x => x.series === state.atlasSeries);
    if (state.atlasRole) list = list.filter(x => (String(x.assetName || '').match(/^role_(\d+)_/i) || [])[1] === state.atlasRole);
    if (state.atlasRarity) list = list.filter(x => effRarity(x.rarity) === state.atlasRarity);
    return list.sort((a, b) => (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
      || String(a.series).localeCompare(String(b.series), 'zh')
      || String(a.name).localeCompare(String(b.name), 'zh'));
  }

  function atlasCard(x, owned) {
    return `<div class="card ${owned ? '' : 'unowned'}" data-key="${x.type}:${x.mainId}" title="${esc(x.name)}">
      ${img(x.thumb, 'thumb', x.name)}
      <div class="rarity-bar ${rarityClass(x.rarity)}"></div>
      <div class="name">${esc(x.name)}</div>
      <div class="sub">${esc(x.series || '')}</div>
      <div class="atlas-badge ${owned ? 'owned' : 'locked'}">${owned ? '已拥有' : '未拥有'}</div>
    </div>`;
  }

  function renderAtlasList(items, ownedNames) {
    const host = document.getElementById('atlasGroups');
    if (!host) return;
    const list = atlasFilteredItems(items);
    if (!list.length) { host.innerHTML = `<div class="empty-state">没有符合条件的图鉴条目</div>`; return; }
    const byKey = new Map(list.map(x => [x.type + ':' + x.mainId, x]));
    if (state.atlasType !== 'all') {
      host.innerHTML = `<div class="bag-grid">${list.map(x => atlasCard(x, ownedNames.has(x.assetName))).join('')}</div>`;
    } else {
      const groups = new Map();
      for (const it of list) {
        const r = effRarity(it.rarity);
        if (!groups.has(r)) groups.set(r, []);
        groups.get(r).push(it);
      }
      const order = ['epic', 'rare', 'uncommon', 'common'];
      host.innerHTML = order.filter(r => groups.has(r)).map(r => {
        const arr = groups.get(r);
        const owned = arr.filter(x => ownedNames.has(x.assetName)).length;
        const hidden = state.atlasHidden.has(r);
        return `<section class="bag-group">
          <div class="bag-group-head" data-agroup="${r}"><span>${RARITY_TEXT[r]}</span><span class="count">（${owned}/${arr.length}）</span><span class="chev">${hidden ? '▸' : '▾'}</span></div>
          ${hidden ? '' : `<div class="bag-grid">${arr.map(x => atlasCard(x, ownedNames.has(x.assetName))).join('')}</div>`}
        </section>`;
      }).join('');
      host.querySelectorAll('[data-agroup]').forEach(el => el.onclick = () => {
        const r = el.dataset.agroup;
        if (state.atlasHidden.has(r)) state.atlasHidden.delete(r); else state.atlasHidden.add(r);
        renderAtlasList(items, ownedNames);
      });
    }
    host.querySelectorAll('.card').forEach(el => {
      el.onclick = () => {
        const x = byKey.get(el.dataset.key);
        if (!x) return;
        const bagItem = (state.bag || []).find(v => v.assetName === x.assetName);
        const currentSlots = [...(state.boot?.appearance?.emoteAssetIds || [])];
        while (currentSlots.length < 5) currentSlots.push(0);
        let pickSlot = currentSlots.findIndex(v => !v);
        if (pickSlot < 0) pickSlot = 0;
        const slotPicker = x.type === 'emote'
          ? `<div class="muted" style="font-size:12px;margin-top:4px">选择要替换的表情槽位（仅本机）</div>
             <div class="emote-row" style="justify-content:center;margin-top:4px">
               ${currentSlots.map((id, i) => {
                 const it = emoteInfo(id);
                 return `<div class="emote-slot ${i === pickSlot ? 'selected' : ''}" data-aslot="${i}" title="槽位 ${i + 1}">${it ? img(it.thumb) : '<span class="muted">空</span>'}<span class="slot-no">${i + 1}</span></div>`;
               }).join('')}
             </div>` : '';
        modal(`
          <div class="reward">
            ${img(x.thumb, '', x.name)}
            <div class="r-name">${esc(x.name)}</div>
            <div class="r-sub">${TYPE_TEXT[x.type] || x.type} · ${RARITY_TEXT[effRarity(x.rarity)] || x.rarity} ${x.series ? '· ' + esc(x.series) : ''}</div>
            <div class="r-sub">${bagItem ? '已拥有 · 可应用到本地，或装备并同步云端' : '尚未拥有 · 可应用到本地（仅自己可见）'}</div>
            ${slotPicker}
            <div style="display:flex;gap:10px;margin-top:8px;flex-wrap:wrap">
              <button class="btn ghost" id="mClose">关闭</button>
              <button class="btn" id="mLocal">应用到本地</button>
              ${bagItem && x.type !== 'emote' ? '<button class="btn ghost" id="mEquip">装备并同步云端</button>' : ''}
            </div>
          </div>`, h => {
          h.querySelector('#mClose').onclick = closeModal;
          h.querySelectorAll('[data-aslot]').forEach(el => el.onclick = () => {
            pickSlot = Number(el.dataset.aslot);
            h.querySelectorAll('[data-aslot]').forEach(e => e.classList.toggle('selected', e === el));
          });
          h.querySelector('#mLocal').onclick = async () => {
            try {
              const data = { type: x.type, id: x.mainId };
              if (x.type === 'emote') data.slot = pickSlot;
              state.boot = await tryCall('appearance.applyLocal', data);
              toast(x.type === 'emote' ? `已替换本地表情槽位 ${pickSlot + 1}` : '已应用到本地（不上传服务端）');
              closeModal();
              renderChrome();
              if (state.page === 'room') renderRoom();
            } catch { }
          };
          const eq = h.querySelector('#mEquip');
          if (eq) eq.onclick = async () => { await equip(bagItem, true); closeModal(); };
        });
      };
    });
  }

  /* ---------------- 合成 ---------------- */
  function synthNeed() { return state.synthMode === 'same' ? 5 : 10; }
  function synthResetSlots() { state.synthSlots = Array(synthNeed()).fill(null); }
  function synthSlotCount(id) { return state.synthSlots.filter(x => x === id).length; }
  function synthBagById(id) { return (state.bag || []).find(x => x.id === id) || null; }
  function synthMaxRarity(type) {
    const order = { common: 1, uncommon: 2, rare: 3, epic: 4 };
    const list = state.atlas[type];
    if (!list || !list.length) return 'epic';
    let max = 'common';
    for (const it of list) if ((order[effRarity(it.rarity)] || 0) > (order[max] || 0)) max = effRarity(it.rarity);
    return max;
  }
  function synthUsable(item) { return Math.max(0, (item.quantity || 1) - 1 - synthSlotCount(item.id)); }
  function synthEligible(type) {
    const maxR = synthMaxRarity(type);
    return (state.bag || []).filter(x => x.type === type)
      .filter(x => state.synthMode === 'same' || effRarity(x.rarity) !== maxR);
  }
  function synthAnchor() { const id = state.synthSlots.find(x => x !== null); return id ? synthBagById(id) : null; }

  async function renderSynth() {
    setPage(`<div class="loading">正在读取背包…</div>`);
    try { await ensureBag(); } catch { setPage('<div class="empty-state">背包读取失败</div>'); return; }
    await ensureAtlas(state.synthType).catch(() => { });
    if (state.synthSlots.length !== synthNeed()) synthResetSlots();

    const need = synthNeed(), type = state.synthType;
    // 清理已不合法/超量的槽位
    const eligibleAll = synthEligible(type);
    const kept = state.synthSlots.map((id, i) => {
      if (id == null) return null;
      const item = eligibleAll.find(x => x.id === id);
      if (!item) return null;
      const before = state.synthSlots.slice(0, i).filter(x => x === id).length;
      return before < Math.max(0, (item.quantity || 1) - 1) ? id : null;
    });
    state.synthSlots = kept.filter(x => x !== null).concat(Array(need - kept.filter(x => x !== null).length).fill(null));

    const rarity = state.synthRarity;
    const items = eligibleAll
      .filter(x => rarity === 'all' || effRarity(x.rarity) === rarity)
      .filter(x => synthUsable(x) > 0 || synthSlotCount(x.id) > 0)
      .sort((a, b) => (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
        || String(a.series).localeCompare(String(b.series), 'zh')
        || String(a.name).localeCompare(String(b.name), 'zh'));

    const filled = state.synthSlots.filter(x => x !== null).length;
    const anchor = synthAnchor();
    const slotHtml = state.synthSlots.map((id, i) => {
      const item = id ? synthBagById(id) : null;
      return `<div class="synth-slot ${item ? 'filled' : ''}" data-slot="${i}" title="${item ? esc(item.name) : '空槽位'}">
        ${item ? img(item.thumb) : ''}<span class="idx">${i + 1}</span></div>`;
    }).join('');

    setPage(`
      <div class="synth-bar">
        <div class="synth-group">
          <span class="synth-label">方式</span>
          ${[['up', '升阶合成（10）'], ['same', '平阶合成（5）']].map(([k, label]) =>
            `<button class="pill ${state.synthMode === k ? 'active' : ''}" data-sm="${k}">${label}</button>`).join('')}
        </div>
        <div class="synth-group">
          <span class="synth-label">类型</span>
          ${['character_skin', 'accessory', 'emote'].map(t =>
            `<button class="pill ${type === t ? 'active' : ''}" data-st="${t}">${TYPE_TEXT[t]}</button>`).join('')}
        </div>
        <div class="synth-group synth-group-rarity">
          <span class="synth-label">稀有度</span>
          ${['all', 'common', 'uncommon', 'rare', 'epic'].map(r =>
            `<button class="pill ${rarity === r ? 'active' : ''}" data-sr="${r}">${r === 'all' ? '全部' : RARITY_TEXT[r]}</button>`).join('')}
        </div>
      </div>
      <div class="synth-slots">${slotHtml}</div>
      <div class="card-grid" id="synthGrid"></div>
      <div class="synth-foot">
        <span class="synth-count">已选 ${filled} / ${need}${anchor ? ' · ' + esc(anchor.name) : ''}</span>
        <span class="spacer"></span>
        <button class="btn ghost" id="synthClear">清空</button>
        <button class="btn ghost" id="synthOne">一键选择</button>
        <button class="btn" id="synthSubmit" ${filled === need ? '' : 'disabled'}>合成</button>
      </div>`);

    document.querySelectorAll('[data-sm]').forEach(b => b.onclick = () => {
      state.synthMode = b.dataset.sm; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-st]').forEach(b => b.onclick = () => {
      state.synthType = b.dataset.st; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-sr]').forEach(b => b.onclick = () => {
      state.synthRarity = b.dataset.sr; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-slot]').forEach(el => el.onclick = () => {
      const idx = Number(el.dataset.slot);
      const next = [...state.synthSlots];
      next[idx] = null;
      state.synthSlots = next.filter(x => x !== null).concat(Array(need - next.filter(x => x !== null).length).fill(null));
      renderSynth();
    });
    document.getElementById('synthClear').onclick = () => { synthResetSlots(); renderSynth(); };
    document.getElementById('synthOne').onclick = () => {
      synthOneClick().catch(e => toast(e.message || '一键选择失败', 'err'));
    };
    document.getElementById('synthSubmit').onclick = doSynth;

    const grid = document.getElementById('synthGrid');
    if (!items.length) {
      grid.innerHTML = `<div class="empty-state">没有可用于合成的重复收集物<br><span style="font-size:12px">合成会保留 1 份，只有第 2 份及以后可以被消耗</span></div>`;
      return;
    }
    grid.innerHTML = items.map(x => {
      const usable = synthUsable(x);
      const sel = synthSlotCount(x.id);
      return `<div class="card ${sel ? 'selected' : ''}" data-id="${x.id}">
        ${img(x.thumb, 'thumb', x.name)}
        <div class="rarity-bar ${rarityClass(x.rarity)}"></div>
        <div class="name" title="${esc(x.name)}">${esc(x.name)}</div>
        <div class="sub">${usable > 0 ? '可用 ×' + usable : '已选满'}</div>
        ${x.quantity > 1 ? `<div class="qty">×${x.quantity}</div>` : ''}
      </div>`;
    }).join('');
    grid.querySelectorAll('.card').forEach(el => {
      el.onclick = () => {
        const item = synthBagById(Number(el.dataset.id));
        if (!item) return;
        if (synthUsable(item) <= 0) return toast('这个收集物没有多余份数可用于合成', 'err');
        if (filled >= need) return toast('槽位已满', 'err');
        const anchorNow = synthAnchor();
        if (anchorNow && (anchorNow.type !== item.type || effRarity(anchorNow.rarity) !== effRarity(item.rarity)))
          return toast('请选择相同类型和稀有度的收集物', 'err');
        const idx = state.synthSlots.findIndex(v => v === null);
        if (idx < 0) return;
        state.synthSlots[idx] = item.id;
        renderSynth();
      };
    });
  }

  async function synthOneClick() {
    const need = synthNeed();
    const filled = state.synthSlots.filter(x => x !== null).length;
    if (filled >= need) return;
    const anchor = synthAnchor();
    // 一键选择搜索全部三类材料，不再受当前“类型”页签限制。
    await Promise.all(['character_skin', 'accessory', 'emote']
      .map(t => ensureAtlas(t).catch(() => [])));
    const pool = ['character_skin', 'accessory', 'emote']
      .flatMap(t => synthEligible(t))
      .filter(x => synthUsable(x) > 0);

    let group;
    let chosenType;
    let chosenRarity;
    if (anchor) {
      chosenType = anchor.type;
      chosenRarity = effRarity(anchor.rarity);
      group = pool.filter(x => x.type === chosenType && effRarity(x.rarity) === chosenRarity);
      if (group.reduce((s, x) => s + synthUsable(x), 0) < need - filled)
        return toast('已有槽位对应的类型/稀有度材料不足', 'err');
    } else {
      const groups = new Map();
      for (const it of pool) {
        const k = it.type + '|' + effRarity(it.rarity);
        if (!groups.has(k)) groups.set(k, []);
        groups.get(k).push(it);
      }
      const orderT = { character_skin: 0, accessory: 1, emote: 2 };
      const orderR = { epic: 4, rare: 3, uncommon: 2, common: 1 };
      const candidates = [...groups.values()]
        .filter(g => g.reduce((s, x) => s + synthUsable(x), 0) >= need)
        .sort((a, b) =>
          orderR[effRarity(b[0].rarity)] - orderR[effRarity(a[0].rarity)]
          || orderT[a[0].type] - orderT[b[0].type]
          || b.reduce((s, x) => s + synthUsable(x), 0) - a.reduce((s, x) => s + synthUsable(x), 0));
      if (!candidates.length)
        return toast('角色 / 配件 / 表情中都没有足够的多余材料', 'err');
      group = candidates[0];
      chosenType = group[0].type;
      chosenRarity = effRarity(group[0].rarity);
    }

    let remaining = need - filled;
    const slots = [...state.synthSlots];
    for (const it of group) {
      const take = Math.min(synthUsable(it), remaining);
      for (let k = 0; k < take; k++) {
        const idx = slots.findIndex(v => v === null);
        if (idx < 0) break;
        slots[idx] = it.id;
      }
      remaining -= take;
      if (remaining <= 0) break;
    }
    state.synthSlots = slots;
    // 让类型/稀有度页签跟随实际选中的材料，否则 renderSynth 会把跨类材料当作无效槽位清掉。
    state.synthType = chosenType;
    state.synthRarity = chosenRarity;
    renderSynth();
  }

  /**
   * 开箱/合成/兑换后刷新背包，并把新物品前插到「最近获得」第一页。
   * 服务器背包列表可能比开箱响应晚一拍；带 acquiredId 时最多重试约 3 秒，
   * 确保面板不因为一次过早的 bag.list 而漏掉刚获得的物品。
   */
  async function refreshAfterAcquire(acquiredId) {
    acquiredId = Number(acquiredId);
    if (!Number.isFinite(acquiredId) || acquiredId <= 0) acquiredId = 0;
    if (acquiredId) markRecentObtained(acquiredId);
    let loaded = false;
    for (let attempt = 0; attempt < 10; attempt++) {
      try {
        const r = await call('bag.list');
        state.bag = r.items || [];
        loaded = true;
        if (!acquiredId || state.bag.some(x => x.id === acquiredId)) break;
      } catch { }
      await new Promise(resolve => setTimeout(resolve, 300));
    }
    if (loaded) {
      state.bagNewReady = false;
      refreshNewAssets();
      if (acquiredId) {
        const owned = new Set((state.bag || []).map(x => x.id));
        if (owned.has(acquiredId)) {
          markRecentObtained(acquiredId);
          state.bagRecentIds = loadRecent().filter(id => owned.has(id));
          state.bagNewIds = loadUnseen().filter(id => owned.has(id));
        }
      }
    }
    if (state.page === 'bag') renderBag();
    if (!loaded) toast('背包刷新失败，请稍后手动刷新', 'err');
  }

  async function doSynth() {
    const need = synthNeed();
    const ids = state.synthSlots.filter(x => x !== null);
    if (ids.length !== need) return toast(`需要 ${need} 个材料`, 'err');
    const sameLevel = state.synthMode === 'same';
    try {
      const r = await tryCall('synthesize', { ids, sameLevel });
      const id = Number(r?.asset_id || r?.data?.asset_id) || 0;
      await refreshAfterAcquire(id);
      const found = (state.bag || []).find(v => v.id === id);
      let thumb = '', name = '合成成功';
      if (found) { thumb = found.thumb; name = found.name; }
      modal(`<div class="reward">
        <div class="rays"></div>
        ${thumb ? img(thumb, 'prize', name) : '<div style="font-size:64px">🎁</div>'}
        <div class="r-name">${esc(name)}</div>
        <div class="r-sub">合成成功</div>
        <button class="btn" id="mOk" style="margin-top:8px">收下</button>
      </div>`, host => host.querySelector('#mOk').onclick = closeModal);
      synthResetSlots();
      renderSynth();
    } catch { }
  }

  /* ---------------- 预设 ---------------- */
  async function ensurePresets(force = false) {
    if (state.presets && !force) return state.presets;
    const data = await tryCall('presets.list');
    state.presets = Array.isArray(data) ? data : (data?.list || []);
    await ensureBag().catch(() => { });
    return state.presets;
  }

  function catalogName(type, id) {
    const bagItem = (state.bag || []).find(v => v.id === id);
    if (bagItem) return bagItem.name;
    const list = state.catalog[type] || [];
    return (list.find(v => v.id === id) || {}).name || (id ? '#' + id : '无');
  }

  async function renderPreset() {
    setPage('<div class="loading">正在读取预设…</div>');
    try { await ensurePresets(); } catch { setPage('<div class="empty-state">预设读取失败</div>'); return; }
    const list = state.presets;
    setPage(`
      <div class="pill-row" style="margin-bottom:14px">
        <span class="muted" style="font-size:13px">预设保存角色、配件与五个表情槽位，可跨房间快速切换。</span>
        <span class="spacer"></span>
        <button class="btn ghost" id="presetRefresh">刷新</button>
        <button class="btn" id="presetCreate">保存当前外观</button>
      </div>
      <div class="preset-list" id="presetList"></div>`);
    document.getElementById('presetRefresh').onclick = async () => { await ensurePresets(true); renderPreset(); };
    document.getElementById('presetCreate').onclick = async () => {
      try { await tryCall('presets.create'); toast('已保存当前外观'); await ensurePresets(true); renderPreset(); } catch { }
    };
    const host = document.getElementById('presetList');
    if (!list.length) { host.innerHTML = '<div class="empty-state">还没有预设，点击「保存当前外观」创建</div>'; return; }
    host.innerHTML = list.map((p, i) => {
      const emotes = (p.emotes || []).map(e => e.asset_id ?? e).filter(Boolean);
      return `<div class="preset-card">
        <div class="preset-head"><span class="preset-emoji">⭐</span><span class="preset-title">预设 ${i + 1}</span></div>
        <div class="detail-row"><span>角色</span><span>${esc(catalogName('character_skin', p.character_asset_id))}</span></div>
        <div class="detail-row"><span>配件</span><span>${esc(catalogName('accessory', p.accessory_asset_id))}</span></div>
        <div class="detail-row"><span>表情</span><span>${emotes.length} 个</span></div>
        <div class="preset-actions">
          <button class="btn ghost" data-apply="${i}">使用</button>
          <button class="btn danger" data-del="${i}">删除</button>
        </div>
      </div>`;
    }).join('');
    host.querySelectorAll('[data-apply]').forEach(b => b.onclick = async () => {
      const p = list[Number(b.dataset.apply)];
      try {
        if (p.character_asset_id) await tryCall('appearance.set', { type: 'character_skin', id: p.character_asset_id });
        if (p.accessory_asset_id) await tryCall('appearance.set', { type: 'accessory', id: p.accessory_asset_id });
        for (const e of (p.emotes || [])) {
          const id = e.asset_id ?? e;
          if (id) await tryCall('appearance.set', { type: 'emote', id });
        }
        await tryCall('appearance.sync');
        toast('已应用预设并同步');
        state.boot = await call('boot');
        renderChrome();
      } catch { }
    });
    host.querySelectorAll('[data-del]').forEach(b => b.onclick = async () => {
      const p = list[Number(b.dataset.del)];
      const id = p.preset_id ?? p.id;
      if (!id) return toast('该预设缺少 id', 'err');
      try { await tryCall('presets.delete', { id }); await ensurePresets(true); renderPreset(); toast('已删除'); } catch { }
    });
  }

  /* ---------------- 联机 ---------------- */
  /** 房间页渲染签名：只有这些字段变化才值得重建 DOM。 */
  function roomSignature(room) {
    if (!room) return '';
    const ms = (room.members || [])
      .map(m => `${m.id}:${m.nickname || ''}:${m.online ? 1 : 0}:${m.visible ? 1 : 0}:${m.owner ? 1 : 0}`)
      .join('|');
    return `${room.state}|${room.roomNo || ''}|${room.connected ? 1 : 0}|${room.hiddenAll ? 1 : 0}|${(room.history || []).join(',')}|${ms}`;
  }

  function renderRoom() {
    const room = state.boot?.room || { state: 'idle', members: [], history: [] };
    state.roomSig = roomSignature(room);
    const joined = room.state === 'joined';
    const auth = state.boot?.auth || {};
    const emotes = state.boot?.appearance?.emoteAssetIds || [];
    setPage(`
      <div class="room-layout">
        <div class="room-left">
          <div class="panel-box">
            <div class="detail-row" style="margin-bottom:6px"><span>当前房间</span><span>${joined ? (room.connected ? '已连接' : '连接中…') : '未加入'}</span></div>
            <div class="room-no">${joined ? esc(room.roomNo) : '——'}<span class="copy" id="copyRoom" ${joined ? '' : 'style="display:none"'}>复制</span></div>
            <div class="detail-row" style="margin-top:8px"><span>可用点击数</span><span data-click-avail>${auth.clicks ?? 0}</span></div>
            <div class="detail-row"><span>累计点击数</span><span data-click-total>${auth.clickTotal ?? auth.clicks ?? 0}</span></div>
            <div style="display:flex;gap:10px;margin-top:14px">
              <button class="btn" id="roomCreate" ${joined ? 'disabled' : ''}>创建房间</button>
              <button class="btn danger" id="roomLeave" ${joined ? '' : 'disabled'}>离开房间</button>
            </div>
          </div>
          <div class="panel-box">
            <div class="section-title" style="margin-left:0">${joined ? '切换房间' : '加入房间'}</div>
            <div style="display:flex;gap:10px">
              <input class="input" id="joinNo" placeholder="输入房间号" style="flex:1" value="${esc(joined ? '' : (state.boot?.settings?.lastRoom || ''))}" />
              <button class="btn" id="roomJoin">${joined ? '切换' : '加入'}</button>
            </div>
            ${joined ? '<div class="hint muted" style="font-size:12px;margin-top:8px">切换会先退出当前房间，再从历史或输入框进入新房间。</div>' : ''}
          </div>
          <div class="panel-box">
            <div class="section-title" style="margin-left:0">历史房间</div>
            ${(room.history || []).length
              ? `<div class="chips">${room.history.slice(0, 20).map(h => `<button class="chip ${h === room.roomNo ? 'active' : ''}" data-hist="${esc(h)}" title="${h === room.roomNo ? '当前房间' : '点击进入'}">${esc(h)}</button>`).join('')}</div>`
              : '<div class="hint muted" style="font-size:12px">还没有历史记录，加入或创建房间后会自动记录。</div>'}
          </div>
          <div class="panel-box">
            <div class="setting-item" style="margin-bottom:0;padding:6px 4px;background:transparent">
              <div><div class="setting-title">隐藏其他成员桌宠</div><div class="setting-sub">只影响本机显示</div></div>
              <div class="switch ${state.boot?.settings?.hideOtherPets ? 'on' : ''}" id="hideOthers"></div>
            </div>
          </div>
          <div class="panel-box">
            <div class="section-title" style="margin-left:0">宝箱</div>
            <div style="display:flex;gap:10px">
              <button class="btn ghost" data-chest="left" style="flex:1">开启左宝箱</button>
              <button class="btn ghost" data-chest="right" style="flex:1">开启右宝箱</button>
            </div>
            <div class="hint muted" style="font-size:12px;margin-top:8px">每次消耗 1000 点击数，冷却 30 分钟，以服务器返回为准。</div>
            <div class="muted" id="chestStatus" style="font-size:13px;margin-top:8px"></div>
          </div>
        </div>
        <div class="room-right">
          <div class="panel-box" style="flex:1;display:flex;flex-direction:column;min-height:0">
            <div style="display:flex;align-items:center;gap:10px">
              <div class="section-title" style="margin-left:0;flex:1">房间成员（${room.members?.length || 0}）</div>
              <button class="btn ghost" id="resetCatOrder" ${joined ? '' : 'disabled'} style="height:30px;padding:0 12px;font-size:13px" title="主角贴右下，其他成员在底部排成一排">重置排序</button>
            </div>
            <div class="member-list" id="memberList">
              ${(room.members || []).map(m => `
                <div class="member ${m.owner ? 'owner' : ''}">
                  <span class="dot ${m.online ? '' : 'off'}"></span>
                  <span class="m-name">${esc(m.nickname || '漂泊者')}</span>
                  ${m.owner ? '<span class="m-role">房主</span>' : ''}
                  ${m.self ? '<span class="m-role">我</span>' : ''}
                  ${!m.online ? '<span class="m-role">离线</span>' : ''}
                  ${!m.self ? (room.hiddenAll
                    ? '<span class="m-role" title="已开启“隐藏其他成员桌宠”">已隐藏</span>'
                    : `<button class="chip" style="margin-left:8px;padding:2px 10px" data-hide="${m.id}" data-visible="${m.visible ? 1 : 0}">${m.visible ? '隐藏' : '显示'}</button>`) : ''}
                </div>`).join('') || '<div class="empty-state">还没有加入房间</div>'}
            </div>
          </div>
          <div class="panel-box">
            <div style="display:flex;align-items:center">
              <div class="section-title" style="margin-left:0;flex:1">快捷表情（仅本地）</div>
              <button class="btn ghost" id="editEmotesBtn" style="height:30px;padding:0 12px;font-size:13px">编辑表情栏</button>
            </div>
            <div class="emote-row" id="emoteRow">
              ${[0, 1, 2, 3, 4].map(i => {
                const id = emotes[i];
                const item = emoteInfo(id);
                return `<div class="emote-slot" data-emote="${id || ''}" title="槽位 ${i + 1}">${item ? img(item.thumb) : '<span class="muted">空</span>'}<span class="slot-no">${i + 1}</span></div>`;
              }).join('')}
            </div>
            <div class="hint muted" style="font-size:12px;margin-top:8px">槽位只保存在本机；发送时服务端不校验归属/角色，可直接发送任意表情。</div>
          </div>
        </div>
      </div>`);

    document.getElementById('copyRoom')?.addEventListener('click', () => {
      navigator.clipboard?.writeText(room.roomNo).then(() => toast('房间号已复制')).catch(() => toast(room.roomNo));
    });
    document.getElementById('roomCreate').onclick = async () => { try { const r = await tryCall('room.create'); state.boot.room = r; renderRoom(); toast('房间已创建'); } catch { } };
    document.getElementById('roomLeave').onclick = async () => { try { const r = await tryCall('room.leave'); state.boot.room = r; renderRoom(); toast('已离开房间'); } catch { } };
    document.getElementById('roomJoin').onclick = async () => {
      const no = document.getElementById('joinNo').value.trim();
      if (!no) return toast('请输入房间号', 'err');
      try { const r = await tryCall('room.join', { roomNo: no }); state.boot.room = r; renderRoom(); toast('已加入房间'); } catch { }
    };
    document.querySelectorAll('[data-hist]').forEach(b => b.onclick = async () => {
      try { const r = await tryCall('room.join', { roomNo: b.dataset.hist }); state.boot.room = r; renderRoom(); toast('已加入房间'); } catch { }
    });
    document.getElementById('resetCatOrder')?.addEventListener('click', async () => {
      try { await tryCall('room.resetOrder'); toast('已重置角色排序'); } catch { }
    });
    document.getElementById('hideOthers').onclick = async () => {
      const hide = !state.boot?.settings?.hideOtherPets;
      try {
        const r = await tryCall('room.hideOthers', { hide });
        if (state.boot) state.boot.settings.hideOtherPets = r?.hiddenAll ?? hide;
        state.boot.room = r; renderRoom();
      } catch { }
    };
    document.querySelectorAll('[data-chest]').forEach(b => b.onclick = async () => {
      try {
        const r = await tryCall('chest.open', { place: b.dataset.chest, clickNum: 1000 });
        const id = Number(r?.asset_id) || 0;
        await refreshAfterAcquire(id);
        let thumb = '', name = '领取成功';
        if (id) {
          const list = await ensureCatalog('character_skin').catch(() => []);
          const found = (state.catalog.character_skin || []).find(v => v.id === id)
            || (state.catalog.accessory || []).find(v => v.id === id)
            || (state.catalog.emote || []).find(v => v.id === id);
          if (found) { thumb = found.thumb; name = found.name; }
        }
        modal(`<div class="reward">
          <div class="rays"></div>
          ${thumb ? img(thumb, 'prize', name) : '<div style="font-size:64px">🎁</div>'}
          <div class="r-name">${esc(name)}</div>
          <div class="r-sub">宝箱奖励</div>
          <button class="btn" id="mOk" style="margin-top:8px">收下</button>
        </div>`, host => host.querySelector('#mOk').onclick = closeModal);
      } catch { }
    });
    document.querySelectorAll('[data-hide]').forEach(b => b.onclick = async () => {
      const id = Number(b.dataset.hide);
      const hide = b.dataset.visible === '1';
      try { const r = await tryCall('room.hideMember', { id, hide }); state.boot.room = r; renderRoom(); } catch { }
    });
    document.getElementById('editEmotesBtn').onclick = () => {
      openEmoteEditor().catch(e => toast('打开编辑器失败：' + e.message, 'err'));
    };
    document.querySelectorAll('[data-emote]').forEach(el => el.onclick = async () => {
      const id = Number(el.dataset.emote);
      if (!id) return toast('该槽位未装备表情', 'err');
      try { await tryCall('emote.send', { id }); toast('已发送表情'); } catch { }
    });
    if (!state.emoteCatalog)
      ensureEmotes().then(() => { if (state.page === 'room') renderRoom(); }).catch(() => { });
  }

  async function ensureEmotes(force = false) {
    if (state.emoteCatalog && state.emoteCatalog.length && !force) return state.emoteCatalog;
    const r = await tryCall('atlas.list', { type: 'emote' });
    const items = (r.items || []).filter(x => x.mainId > 0);
    if (items.length) state.emoteCatalog = items;
    return items;
  }

  function emoteInfo(id) {
    if (!id) return null;
    const fromCatalog = (state.emoteCatalog || []).find(x => x.mainId === id);
    if (fromCatalog) return fromCatalog;
    const fromBag = (state.bag || []).find(x => x.id === id);
    if (fromBag) return fromBag;
    return null;
  }

  async function openEmoteEditor() {
    let list = [];
    for (let attempt = 0; attempt < 6 && !list.length; attempt++) {
      try { list = await ensureEmotes(attempt > 0); } catch { list = []; }
      if (!list.length) await new Promise(r => setTimeout(r, 700));
    }
    if (!list.length) { toast('表情目录尚未就绪，请稍后再试', 'err'); return; }
    let ids = [...(state.boot?.appearance?.emoteAssetIds || [])];
    while (ids.length < 5) ids.push(0);
    let slot = 0;

    const byId = new Map(list.map(x => [x.mainId, x]));
    const info = id => byId.get(id) || emoteInfo(id);

    modal(`<div style="display:flex;flex-direction:column;gap:10px">
      <div style="display:flex;align-items:center;gap:10px">
        <div style="font-size:18px;font-weight:800;color:var(--ink-strong)">编辑表情栏</div>
        <span class="muted" style="font-size:12px">仅本机 · 可拖拽 / 点选</span>
        <span class="spacer"></span>
        <button class="btn ghost" id="eeClose">关闭</button>
      </div>
      <div class="emote-row" id="eeSlots" style="align-items:flex-start"></div>
      <div style="display:flex;gap:8px;align-items:center">
        <input class="input" id="eeSearch" placeholder="搜索表情 / 角色" style="flex:1" />
        <span class="muted" id="eeStatus" style="font-size:12px;white-space:nowrap"></span>
      </div>
      <div class="emote-editor-grid" id="eeGrid"></div>
      <div class="muted" style="font-size:12px">点一个槽位再点表情即可替换；也可以把表情直接拖到槽位，或把槽位拖到另一个槽位交换。</div>
    </div>`, host => {
      const slotsEl = host.querySelector('#eeSlots');
      const gridEl = host.querySelector('#eeGrid');
      const statusEl = host.querySelector('#eeStatus');
      const search = host.querySelector('#eeSearch');

      const paint = () => {
        slotsEl.innerHTML = ids.map((id, i) => {
          const it = info(id);
          return `<div class="emote-slot ${i === slot ? 'selected' : ''}" data-slot="${i}" draggable="${it ? 'true' : 'false'}" title="槽位 ${i + 1}">
            ${it ? img(it.thumb) : '<span class="muted">空</span>'}
            <span class="slot-no">${i + 1}</span>
            ${it ? `<button class="slot-clear" data-clear="${i}" title="清空">×</button>` : ''}
          </div>`;
        }).join('');
        statusEl.textContent = `当前槽位 ${slot + 1}`;
        gridEl.querySelectorAll('.ee-card').forEach(el => el.classList.toggle('in-slot', ids.includes(Number(el.dataset.eid))));

        slotsEl.querySelectorAll('[data-slot]').forEach(el => {
          el.onclick = e => { if (e.target.closest('[data-clear]')) return; slot = Number(el.dataset.slot); paint(); };
          el.ondragstart = e => {
            const i = Number(el.dataset.slot);
            if (!info(ids[i])) { e.preventDefault(); return; }
            e.dataTransfer.setData('text/plain', 'slot:' + i);
            e.dataTransfer.effectAllowed = 'move';
          };
          el.ondragover = e => { e.preventDefault(); el.classList.add('drag-over'); };
          el.ondragleave = () => el.classList.remove('drag-over');
          el.ondrop = async e => {
            e.preventDefault();
            el.classList.remove('drag-over');
            const data = e.dataTransfer.getData('text/plain');
            const target = Number(el.dataset.slot);
            if (data.startsWith('slot:')) {
              const from = Number(data.slice(5));
              if (from === target) return;
              const a = ids[from], b = ids[target];
              await setSlot(from, b); await setSlot(target, a);
            } else if (data) {
              await setSlot(target, Number(data));
            }
          };
        });
        slotsEl.querySelectorAll('[data-clear]').forEach(b => b.onclick = async e => {
          e.stopPropagation();
          await setSlot(Number(b.dataset.clear), 0);
        });
      };

      const setSlot = async (i, id) => {
        try {
          state.boot = await tryCall('appearance.setSlot', { slot: i, id });
          ids[i] = id;
          slot = i;
          paint();
        } catch { }
      };

      const renderGrid = () => {
        const q = search.value.trim().toLowerCase();
        const items = list.filter(x => !q || (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
        gridEl.innerHTML = items.map(x => `<div class="ee-card" draggable="true" data-eid="${x.mainId}" title="${esc(x.name)}">${img(x.thumb)}<div class="ee-name">${esc(x.name)}</div></div>`).join('');
        gridEl.querySelectorAll('.ee-card').forEach(el => {
          el.onclick = () => setSlot(slot, Number(el.dataset.eid));
          el.ondragstart = e => {
            e.dataTransfer.setData('text/plain', el.dataset.eid);
            e.dataTransfer.effectAllowed = 'copy';
          };
        });
        paint();
      };

      host.querySelector('#eeClose').onclick = () => { closeModal(); if (state.page === 'room') renderRoom(); };
      search.oninput = renderGrid;
      renderGrid();
    });
  }

  function chestTimeText(ts) {
    if (!ts) return '未生成';
    const left = ts - Date.now();
    if (left <= 0) return '可开启';
    const s = Math.ceil(left / 1000), m = Math.floor(s / 60), ss = s % 60;
    return `${m}分${String(ss).padStart(2, '0')}秒后可开启`;
  }

  function updateChestStatus() {
    const el = document.getElementById('chestStatus');
    if (!el) return;
    const a = state.boot?.auth || {};
    el.textContent = `左侧宝箱：${chestTimeText(a.chestLeft)}　右侧宝箱：${chestTimeText(a.chestRight)}`;
  }

  /* ---------------- 设置 ---------------- */
  function renderSettings() {
    const s = state.boot?.settings || {};
    const auth = state.boot?.auth || {};
    // 快捷键/菜单改设置会触发 boot 重推；保留滚动位置，别把用户顶回顶部。
    const prevScroll = document.querySelector('.settings-scroll')?.scrollTop || 0;
    setPage(`
      <div class="settings-scroll">
        <div class="section">
          <div class="section-title">账号</div>
          <div class="panel-box" style="display:flex;flex-direction:column;gap:12px">
            ${isLogged() ? `
              <div class="detail-row"><span>昵称</span><span>${esc(auth.nickname || '—')}</span></div>
              <div class="detail-row"><span>UID</span><span>${auth.uid || '—'}</span></div>
              <div class="detail-row"><span>可用点击</span><span data-click-avail>${auth.clicks || 0}</span></div>
              <div class="detail-row"><span>累计点击</span><span class="click-total"><span data-click-total>${auth.clickTotal ?? auth.clicks ?? 0}</span><button class="btn ghost click-boost" id="boostClicks" title="本地增加 10000 次点击并立即上报；连续操作间隔至少 0.5 秒">+10000</button></span></div>
              <div class="detail-row"><span>已消耗</span><span data-click-consume>${auth.clickConsume || 0}</span></div>
              <div style="display:flex;gap:10px">
                <button class="btn ghost" id="userRefresh">刷新账号信息</button>
                <button class="btn danger" id="logoutBtn">退出登录</button>
              </div>` : `
              <div class="detail-row"><span>登录</span><span class="muted">UID + 验证码（也可继承官方登录状态）</span></div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setUid" placeholder="游戏 UID（9-10 位）" style="width:180px" />
                <input class="input" id="setCode" placeholder="6 位验证码" style="width:140px" />
                <button class="btn ghost" id="setSend">发送验证码</button>
                <button class="btn" id="setLogin">登录</button>
              </div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setToken" placeholder="粘贴官方客户端 infoToken" style="flex:1" />
                <button class="btn ghost" id="setTokenBtn">导入 token</button>
              </div>`}
            <div class="inherit-login">
              <div class="setting-sub">建议在获取前先关闭本家程序的开机自动启动，以免下次开机因为 token 冲突导致掉线。</div>
              <button class="btn ghost" id="inheritLogin">继承官方程序登录状态</button>
            </div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">窗口与桌宠</div>
          <div class="setting-item">
            <div><div class="setting-title">桌宠大小</div><div class="setting-sub">缩放以猫底部中心为锚点</div></div>
            <div class="setting-content">
              <select class="select" id="setScale">
                ${[0.25, 0.5, 0.75, 1, 1.25, 1.5, 2].map(v => `<option value="${v}" ${Math.abs((s.scale || 1) - v) < 0.01 ? 'selected' : ''}>${Math.round(v * 100)}%</option>`).join('')}
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">桌宠不透明度</div><div class="setting-sub">100% 为完全不透明</div></div>
            <div class="setting-content">
              <input type="range" min="20" max="100" value="${Math.round((s.opacity ?? 1) * 100)}" id="setOpacity" style="width:180px" />
              <span id="opacityVal">${Math.round((s.opacity ?? 1) * 100)}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">窗口置顶</div><div class="setting-sub">桌宠始终显示在其他窗口上方</div></div>
            <div class="switch ${s.topmost ? 'on' : ''}" data-switch="topmost"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">游戏模式（鼠标穿透）</div><div class="setting-sub">开启后桌宠不响应鼠标，可从托盘菜单恢复</div></div>
            <div class="switch ${s.clickThrough ? 'on' : ''}" data-switch="clickThrough"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">鼠标悬停时半透明</div><div class="setting-sub">便于看清桌宠后的内容</div></div>
            <div class="switch ${s.hoverFade ? 'on' : ''}" data-switch="hoverFade"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">允许角色重叠</div><div class="setting-sub">默认开启；关闭后检测到重叠的联机桌宠会自动挪到旁边</div></div>
            <div class="switch ${s.allowOverlap !== false ? 'on' : ''}" data-switch="allowOverlap"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">OBS 绿幕窗口</div><div class="setting-sub">主角完整 GUI（数字/计数条/宝箱/昵称）和键盘/手柄面板一起显示在纯绿窗口中；带标题栏，可拖动/拉边调整</div></div>
            <div class="switch ${s.captureMode ? 'on' : ''}" data-switch="captureMode"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">绿幕窗口大小</div><div class="setting-sub">单位像素，指绿色客户区大小；OBS 窗口采集选择 “Wuwatappo Capture”</div></div>
            <div class="setting-content">
              <input class="input" id="captureW" type="number" min="160" max="1600" step="10" value="${s.captureWidth ?? 400}" style="width:86px" />
              <span class="muted">×</span>
              <input class="input" id="captureH" type="number" min="160" max="1600" step="10" value="${s.captureHeight ?? 700}" style="width:86px" />
            </div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">桌面键盘</div>
          <div class="setting-item">
            <div><div class="setting-title">音游模式</div><div class="setting-sub">把鸣潮专属指示器替换四键音游指示器；开启后立即切换</div></div>
            <div class="switch ${s.rhythmMode ? 'on' : ''}" data-switch="rhythmMode"></div>
          </div>
          ${s.rhythmMode ? `<div class="setting-item">
            <div><div class="setting-title">音游触发键位</div><div class="setting-sub">从左到右四个键；显示对应字母，只接受单个字母或数字且不能重复</div></div>
            <div class="setting-content">
              ${[0, 1, 2, 3].map(i => `<input class="input" data-rhythm-key="${i}" maxlength="1" value="${esc(s.rhythmKeys?.[i] || ['D', 'F', 'J', 'K'][i])}" style="width:46px;text-align:center;text-transform:uppercase;font-weight:700" inputmode="text" autocomplete="off" />`).join('')}
            </div>
          </div>` : ''}
          <div class="setting-item">
            <div><div class="setting-title">键盘映射</div><div class="setting-sub">桌面键盘完全穿透，不拦截输入</div></div>
            <div class="setting-content">
              ${[['hidden', '不显示'], ['letters', '26 键'], s.rhythmMode ? ['rhythm', '音游模式'] : ['wuwa', '鸣潮专属'], ['controller', '手柄']].map(([k, label]) =>
                `<button class="pill ${s.keyboardMode === k ? 'active' : ''}" data-kb="${k}">${label}</button>`).join('')}
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">显示映射控制</div><div class="setting-sub">关闭后仅保留键位图案</div></div>
            <div class="switch ${s.showMappingControls ? 'on' : ''}" data-switch="showMappingControls"></div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">快捷键</div>
          <div class="hint muted" style="font-size:12px;margin:-4px 0 10px">点击输入框后按下新组合键；Esc 取消，退格清除（禁用该快捷键）。支持 Ctrl / Alt / Shift + 普通按键。快捷键为被动监听，不会拦截其他程序的按键，也不影响按键延迟。</div>
          <div class="hotkey-list" id="hotkeyList">
            ${HOTKEY_ACTIONS.map(a => `
              <div class="hotkey-row">
                <span class="hotkey-label">${esc(a.label)}</span>
                <input class="input hotkey-input" readonly data-hotkey="${a.id}" value="${esc((s.hotkeys || {})[a.id] || '未设置')}" />
                <button class="btn ghost hotkey-clear" data-hotkey-clear="${a.id}">清除</button>
              </div>`).join('')}
          </div>
          <div style="display:flex;gap:10px;margin-top:10px">
            <button class="btn" id="hotkeysSave">保存快捷键</button>
            <button class="btn ghost" id="hotkeysReset">恢复默认</button>
          </div>
        </div>

        <div class="section">
          <div class="section-title">联机与宝箱</div>
          <div class="setting-item">
            <div><div class="setting-title">隐藏其他成员桌宠</div><div class="setting-sub">本机显示设置，不影响他人</div></div>
            <div class="switch ${s.hideOtherPets ? 'on' : ''}" data-switch="hideOtherPets"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">总是显示宝箱</div><div class="setting-sub">显示宝箱倒计时</div></div>
            <div class="switch ${s.alwaysShowChest ? 'on' : ''}" data-switch="alwaysShowChest"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">自动领取宝箱</div><div class="setting-sub">倒计时结束后至少等待 5 秒再领取</div></div>
            <div class="switch ${s.autoChest ? 'on' : ''}" data-switch="autoChest"></div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">通知</div>
          <div class="setting-item">
            <div><div class="setting-title">宝箱领取通知</div><div class="setting-sub">自动领取成功后发送 Windows 通知</div></div>
            <div class="switch ${s.notifyChestClaimed !== false ? 'on' : ''}" data-switch="notifyChestClaimed"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">快捷键操作通知</div><div class="setting-sub">触发刷新、游戏模式等快捷键时发送 Windows 通知</div></div>
            <div class="switch ${s.notifyHotkeys !== false ? 'on' : ''}" data-switch="notifyHotkeys"></div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">兑换</div>
          <div class="panel-box" style="display:flex;gap:10px">
            <input class="input" id="redeemCode" placeholder="输入兑换码" style="flex:1" />
            <button class="btn" id="redeemBtn">兑换</button>
          </div>
        </div>

        <div class="section">
          <div class="section-title">其他</div>
          <div class="setting-item">
            <div><div class="setting-title">开机自动启动</div><div class="setting-sub">登录 Windows 后自动运行桌宠（仅当前用户，不写系统级启动项）</div></div>
            <div class="switch ${s.launchAtLogin ? 'on' : ''}" data-switch="launchAtLogin"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">以管理员权限启动</div><div class="setting-sub">${s.isElevated ? '当前已是管理员权限' : '当前为普通权限'}；用于统计以管理员权限运行的游戏/程序中的按键。开启会请求 UAC 并重启（开机启动时也会请求）</div></div>
            <div class="switch ${s.runAsAdmin ? 'on' : ''}" data-switch="runAsAdmin"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">日志与数据</div><div class="setting-sub">%LOCALAPPDATA%\\WuwatappoNative</div></div>
            <div class="setting-content"><button class="btn ghost" id="openLog">打开日志目录</button></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">关于</div><div class="setting-sub">版本 ${esc(state.boot?.version || '0.0.1')} · 免责声明与鸣谢</div></div>
            <div class="setting-content"><button class="btn ghost" id="aboutBtn">关于本程序</button></div>
          </div>
        </div>
      </div>`);

    bindSettings();
    const scroller = document.querySelector('.settings-scroll');
    if (scroller && prevScroll > 0) scroller.scrollTop = prevScroll;
  }

  // 与原版 Helper 一致的默认键位；实际值来自 boot.settings.hotkeys。
  const HOTKEY_ACTIONS = [
    { id: 'game', label: '游戏模式（鼠标穿透）', def: 'F7' },
    { id: 'monitor', label: '切换屏幕', def: 'F8' },
    { id: 'visibility', label: '显示／隐藏桌宠', def: 'F9' },
    { id: 'reload', label: '刷新界面', def: 'F5' },
    ...[1, 2, 3, 4, 5, 6].map(i => ({ id: 'emote' + i, label: '发送表情 · 槽位 ' + i, def: 'Ctrl+Alt+' + i })),
  ];

  function hotkeyFromEvent(e) {
    if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(e.key)) return null;
    if (e.metaKey) return null;
    let base = '';
    if (e.code && /^Key[A-Z]$/.test(e.code)) base = e.code.slice(3);
    else if (e.code && /^Digit\d$/.test(e.code)) base = e.code.slice(5);
    else if (/^F([1-9]|1[0-2])$/.test(e.key)) base = e.key.toUpperCase();
    else {
      const map = { ' ': 'Space', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Escape: 'Escape', Enter: 'Enter', Tab: 'Tab', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', '`': '`', '-': '-', '=': '=', '[': '[', ']': ']', ';': ';', "'": "'", ',': ',', '.': '.', '/': '/', '\\': '\\' };
      base = map[e.key] || '';
    }
    if (!base) return null;
    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');
    parts.push(base);
    return parts.join('+');
  }

  function bindHotkeys() {
    const rows = {};
    document.querySelectorAll('[data-hotkey]').forEach(input => {
      const id = input.dataset.hotkey;
      rows[id] = input;
      let listening = false;
      input.addEventListener('focus', () => { listening = true; input.value = '请按下新快捷键…'; });
      input.addEventListener('blur', () => {
        listening = false;
        if (input.value === '请按下新快捷键…') input.value = (state.boot?.settings?.hotkeys || {})[id] || '未设置';
      });
      input.addEventListener('keydown', e => {
        if (!listening) return;
        e.preventDefault(); e.stopPropagation();
        if (e.key === 'Escape') { input.blur(); return; }
        if (e.key === 'Backspace' || e.key === 'Delete') { input.value = '未设置'; input.dataset.value = ''; input.blur(); return; }
        const chord = hotkeyFromEvent(e);
        if (!chord) return;
        input.value = chord;
        input.dataset.value = chord;
        input.blur();
      });
    });
    document.querySelectorAll('[data-hotkey-clear]').forEach(btn => btn.onclick = () => {
      const input = rows[btn.dataset.hotkeyClear];
      if (input) { input.value = '未设置'; input.dataset.value = ''; }
    });
    document.getElementById('hotkeysSave')?.addEventListener('click', async () => {
      const table = {};
      for (const a of HOTKEY_ACTIONS) {
        const input = rows[a.id];
        if (!input) continue;
        const v = input.dataset.value ?? ((state.boot?.settings?.hotkeys || {})[a.id] || '');
        table[a.id] = input.value === '未设置' ? '' : v;
      }
      try {
        const r = await tryCall('hotkeys.save', { hotkeys: table });
        if (r?.ok === false) return toast(r.error || '保存失败', 'err');
        await syncBoot();
        renderSettings();
        toast('快捷键已保存');
      } catch { }
    });
    document.getElementById('hotkeysReset')?.addEventListener('click', async () => {
      const table = {};
      for (const a of HOTKEY_ACTIONS) table[a.id] = a.def;
      try {
        const r = await tryCall('hotkeys.save', { hotkeys: table });
        if (r?.ok === false) return toast(r.error || '保存失败', 'err');
        await syncBoot();
        renderSettings();
        toast('已恢复默认快捷键');
      } catch { }
    });
  }

  function bindSettings() {
    const val = (id) => document.getElementById(id)?.value;
    document.getElementById('setScale')?.addEventListener('change', async e => {
      try { await tryCall('settings.set', { scale: Number(e.target.value) }); await syncBoot(); } catch { }
    });
    const captureW = document.getElementById('captureW');
    const captureH = document.getElementById('captureH');
    const applyCaptureSize = async () => {
      if (!captureW || !captureH) return;
      const w = Math.min(1600, Math.max(160, Math.round(Number(captureW.value) || 400)));
      const h = Math.min(1600, Math.max(160, Math.round(Number(captureH.value) || 700)));
      captureW.value = w; captureH.value = h;
      try { await tryCall('settings.set', { captureWidth: w, captureHeight: h }); await syncBoot(); } catch { }
    };
    captureW?.addEventListener('change', applyCaptureSize);
    captureH?.addEventListener('change', applyCaptureSize);
    const op = document.getElementById('setOpacity');
    op?.addEventListener('input', () => { document.getElementById('opacityVal').textContent = op.value + '%'; });
    op?.addEventListener('change', async () => {
      try { await tryCall('settings.set', { opacity: Number(op.value) / 100 }); await syncBoot(); } catch { }
    });
    document.querySelectorAll('[data-switch]').forEach(el => el.onclick = async () => {
      const key = el.dataset.switch;
      const next = !el.classList.contains('on');
      el.classList.toggle('on', next);
      if (key === 'runAsAdmin' && next) toast('正在请求管理员权限…');
      try {
        await tryCall('settings.set', { [key]: next });
        await syncBoot();
        if (key === 'rhythmMode') { renderSettings(); return; }
        if (key === 'runAsAdmin') {
          const saved = !!state.boot?.settings?.runAsAdmin;
          const elevated = !!state.boot?.settings?.isElevated;
          renderSettings();
          if (next && !saved) toast('已取消管理员授权，继续使用普通权限', 'err');
          else if (!next && elevated) toast('已关闭，重启后以普通权限运行');
          else if (next && elevated) toast('已启用管理员权限启动');
        }
      } catch { el.classList.toggle('on', !next); }
    });
    document.querySelectorAll('[data-kb]').forEach(el => el.onclick = async () => {
      try { await tryCall('settings.set', { keyboardMode: el.dataset.kb }); await syncBoot(); renderSettings(); } catch { }
    });
    const rhythmInputs = [...document.querySelectorAll('[data-rhythm-key]')];
    rhythmInputs.forEach(input => {
      input.addEventListener('input', () => {
        input.value = String(input.value || '').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 1);
      });
      input.addEventListener('change', async () => {
        const keys = rhythmInputs.map(x => String(x.value || '').trim().toUpperCase());
        if (keys.some(k => !/^[A-Z0-9]$/.test(k))) return toast('四个键位都必须是单个字母或数字', 'err');
        if (new Set(keys).size !== 4) return toast('四个触发键位不能重复', 'err');
        try {
          await tryCall('settings.set', { rhythmKeys: keys });
          await syncBoot();
          renderSettings();
          toast('音游键位已保存');
        } catch { }
      });
    });
    document.getElementById('openLog')?.addEventListener('click', () => call('open.log'));
    document.getElementById('aboutBtn')?.addEventListener('click', showAbout);
    bindHotkeys();
    document.getElementById('redeemBtn')?.addEventListener('click', async () => {
      const code = document.getElementById('redeemCode').value.trim();
      if (!code) return toast('请输入兑换码', 'err');
      try { await tryCall('redeem', { code }); toast('兑换成功'); document.getElementById('redeemCode').value = ''; } catch { }
    });
    document.getElementById('logoutBtn')?.addEventListener('click', async () => {
      try { await tryCall('logout'); state.boot = await call('boot'); renderChrome(); navigate('bag'); toast('已退出登录'); } catch { }
    });
    document.getElementById('userRefresh')?.addEventListener('click', async () => {
      try { state.boot = await tryCall('user.refresh'); renderChrome(); renderSettings(); toast('已刷新'); } catch { }
    });
    const boost = document.getElementById('boostClicks');
    if (boost) {
      let busy = false;
      boost.onclick = async () => {
        if (busy) return;
        busy = true;
        boost.disabled = true;
        const started = performance.now();
        try {
          const r = await tryCall('clicks.boost', { delta: 10000 });
          if (r?.accepted) {
            applyClicks(r);
            toast('点击数 +10000');
          } else {
            toast('操作太快，请稍后再试', 'err');
          }
        } catch { }
        // 原生层也有 500ms 限流；按钮保持禁用相同时间，避免连点堆积。
        const wait = Math.max(0, 550 - (performance.now() - started));
        setTimeout(() => {
          busy = false;
          if (boost.isConnected) boost.disabled = false;
        }, wait);
      };
    }
    document.getElementById('setSend')?.addEventListener('click', async () => {
      const uid = val('setUid');
      if (!uid) return toast('请输入游戏 UID', 'err');
      if (!/^[0-9]{9,10}$/.test(uid)) return toast('请输入 9-10 位游戏 UID', 'err');
      try { await tryCall('login.sendCode', { uid }); toast('验证码已发送'); } catch { }
    });
    document.getElementById('setLogin')?.addEventListener('click', async () => {
      const uid = val('setUid'), code = val('setCode');
      if (!uid || !code) return toast('请输入游戏 UID 与验证码', 'err');
      if (!/^[0-9]{9,10}$/.test(uid)) return toast('请输入 9-10 位游戏 UID', 'err');
      if (!/^[0-9]{6}$/.test(code)) return toast('请输入 6 位数字验证码', 'err');
      try { await tryCall('login.submit', { uid, code }); state.boot = await call('boot'); renderChrome(); navigate('bag'); toast('登录成功'); } catch { }
    });
    document.getElementById('inheritLogin')?.addEventListener('click', showInheritLogin);
    document.getElementById('setTokenBtn')?.addEventListener('click', async () => {
      const token = val('setToken');
      if (!token) return toast('请输入 token', 'err');
      try { await tryCall('login.token', { token }); state.boot = await call('boot'); renderChrome(); navigate('bag'); toast('已导入并登录'); } catch { }
    });
  }

  async function syncBoot() {
    state.boot = await call('boot');
    renderChrome();
  }

  init();
  boot();
})();
