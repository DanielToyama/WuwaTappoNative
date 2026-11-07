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
    update: null,
    updateDismissed: false,
    compatibility: null,
    announcements: [],
    loginMode: 'uid',
    favors: new Set(),
    catalog: {},
    atlas: {},
    atlasType: 'all',
    atlasSeries: '',
    atlasRole: '',
    atlasRarity: '',
    atlasHidden: new Set(),
    atlasAllCount: 0,
    atlasOwnedOnly: false,
    atlasSelected: null,
    synthMode: 'up',
    synthType: 'character_skin',
    synthRarity: 'all',
    synthSlots: [],
    synthCanCraft: false,
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

  const PARAMS = new URLSearchParams(location.search);
  const PREVIEW = PARAMS.get('preview');
  const DEMO = PARAMS.has('demo') || !!PREVIEW;

  function isLogged() { return DEMO || !!(state.boot && state.boot.auth && state.boot.auth.loggedIn); }

  /* ---------------- 初始化 ---------------- */
  const HASH_PARAMS = new URLSearchParams(location.hash.replace(/^#/, ''));

  function init() {
    const hashParams = HASH_PARAMS;
    const initialPage = hashParams.get('page');
    if (initialPage && PAGES.some(p => p.key === initialPage)) state.page = initialPage;
    document.getElementById('tabs').innerHTML = PAGES.map(p =>
      `<button class="tab" data-page="${p.key}">${ICONS[p.icon]}<span>${p.title}</span>${p.key === 'synth' ? '<span class="tab-dot" data-tab-badge="synth" title="有可合成物资"></span>' : ''}</button>`).join('');
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
      state.update = b.update || state.update;
      state.announcements = Array.isArray(b.announcements) ? b.announcements : [];
      renderChrome();
      renderPage();
      if (isLogged()) ensureBag().catch(() => { });
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
          <p>${link(state.boot?.settings?.serverRegion === 'global' ? 'https://download-wutheringwaves-wuwatappo.kurogames-global.com/download/en/' : 'https://download-mc-wuwatappo.kurogames-global.com/download/', '库洛游戏')}</p>
          <p>${link('https://github.com/Fantasicrap/', '@Fantasicrap')}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">演示视频</div>
          <p>${link('https://www.bilibili.com/video/BV1nRxk6dERS/', 'Bilibili 演示视频')}</p>
          <p>如果可以请给我一键三连，这对我非常重要。</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">项目地址</div>
          <p>GitHub：${link('https://github.com/DanielToyama/WuwaTappoNative', 'DanielToyama/WuwaTappoNative')}</p>
          <p>希望收到你的 Star！⭐</p>
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
          <p>继续后会读取本机官方 WuwaTappo 客户端的登录数据；如果官方客户端正在运行，会被关闭。读取到的登录凭据只会发送给库洛官方服务器用于换取登录 token，不会发送到其他地址。</p>
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
    if (evt === 'boot') { state.boot = data; state.update = data.update || state.update; state.compatibility = data.compatibility || state.compatibility; state.announcements = Array.isArray(data.announcements) ? data.announcements : state.announcements; renderChrome(); renderPage(); maybeShowDisclaimer(); maybeShowUacPrompt(); maybeOpenEmoteEditor(); return; }
    if (evt === 'navigate') { if (data?.page) navigate(data.page); return; }
    if (evt === 'announcements') {
      state.announcements = Array.isArray(data) ? data : [];
      renderNotices();
      return;
    }
    if (evt === 'compatibility') {
      state.compatibility = data;
      renderNotices();
      if (state.page === 'settings') renderSettings();
      return;
    }
    if (evt === 'update') {
      state.update = data;
      if (state.page === 'settings') renderSettings();
      return;
    }
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
    updateSynthBadge();
    renderNotices();
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
    const reg = state.boot?.settings?.serverRegion || 'cn';
    const isGlobal = reg === 'global';
    const mode = !isGlobal && state.loginMode === 'phone' ? 'phone' : 'uid';
    return `<div class="login-wrap"><div class="login-card">
      <h2>登录 Wuwa Tappo</h2>
      <div style="display:flex;align-items:center;gap:10px;margin-bottom:12px">
        <span style="font-size:13px;color:var(--text-sub,#888);white-space:nowrap">区服：</span>
        <select class="select" id="loginRegion" style="flex:1">
          <option value="cn" ${!isGlobal ? 'selected' : ''}>国服（CN）</option>
          <option value="global" ${isGlobal ? 'selected' : ''}>国际服（Global）</option>
        </select>
      </div>

      ${isGlobal ? `
        <div class="hint" style="color:var(--text-sub,#888);line-height:1.6;margin-bottom:14px;background:rgba(0,0,0,0.03);padding:10px 12px;border-radius:6px;border:1px solid var(--border,#e0d4c3)">
          <strong>国际服提示：</strong>国际服不支持游戏内 UID 邮件验证码。请先在电脑上登录官方 WuwaTappo 国际服客户端，再点击下方按钮继承登录状态。
        </div>

        <button class="btn" id="loginInherit" style="margin-bottom:14px;width:100%">继承官方程序登录状态</button>

        <div style="font-size:12px;color:var(--text-sub,#888);margin-bottom:6px">或手动输入 Token 登录：</div>
        <div style="display:flex;gap:10px">
          <input class="input" id="loginTokenInput" placeholder="输入 kuro_infoToken / infoToken" style="flex:1" />
          <button class="btn ghost" id="loginTokenBtn">Token 登录</button>
        </div>
      ` : `
        <div class="pill-row" style="margin-bottom:12px">
          <button type="button" class="pill ${mode === 'uid' ? 'active' : ''}" data-login-mode="uid">UID 验证码</button>
          <button type="button" class="pill ${mode === 'phone' ? 'active' : ''}" data-login-mode="phone">手机号登录</button>
        </div>
        ${mode === 'phone' ? `
          <div class="hint">使用库洛通行证手机号登录；验证码由库洛发送，可能需要人机验证。</div>
          <input class="input" id="loginPhone" placeholder="手机号（11 位）" inputmode="numeric" maxlength="11" autocomplete="tel" />
          <div style="display:flex;gap:10px">
            <input class="input" id="loginPhoneCode" placeholder="短信验证码" style="flex:1" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
            <button class="btn ghost" id="phoneSendBtn">发送验证码</button>
          </div>
          <button class="btn" id="phoneLoginBtn">手机号登录</button>
          <div class="hint">登录成功后会同步该通行证绑定的鸣潮角色。</div>
        ` : `
          <div class="hint">使用游戏 UID 登录；如果无法完成验证码登录，请使用官方登录状态。</div>
          <input class="input" id="loginUid" placeholder="游戏 UID（9-10 位）" inputmode="numeric" maxlength="10" />
          <div style="display:flex;gap:10px">
            <input class="input" id="loginCode" placeholder="6 位验证码" style="flex:1" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
            <button class="btn ghost" id="sendCodeBtn">发送验证码</button>
          </div>
          <button class="btn" id="loginBtn">登录</button>
        `}
        <div class="hint">如果无法完成验证码登录，请先在官方客户端登录，再点击下方按钮继承官方登录状态。</div>
        <button class="btn ghost" id="loginInherit">继承官方程序登录状态</button>

        <div style="margin-top:12px;border-top:1px dashed var(--border,#e0d4c3);padding-top:10px">
          <div style="display:flex;gap:10px">
            <input class="input" id="loginTokenInput" placeholder="手动输入 Token 登录" style="flex:1" />
            <button class="btn ghost" id="loginTokenBtn">Token 登录</button>
          </div>
        </div>
      `}
    </div></div>`;
  }

  function bindLogin() {
    const send = document.getElementById('sendCodeBtn');
    const go = document.getElementById('loginBtn');
    const uidEl = document.getElementById('loginUid');
    const codeEl = document.getElementById('loginCode');
    const phoneEl = document.getElementById('loginPhone');
    const phoneCodeEl = document.getElementById('loginPhoneCode');
    const phoneSend = document.getElementById('phoneSendBtn');
    const phoneLogin = document.getElementById('phoneLoginBtn');
    const regEl = document.getElementById('loginRegion');
    const startCooldown = (btn, seconds, label) => {
      if (!btn) return;
      let left = seconds;
      btn.disabled = true;
      btn.textContent = left + 's';
      const timer = setInterval(() => {
        left--;
        if (left <= 0) {
          clearInterval(timer);
          btn.disabled = false;
          btn.textContent = label;
        } else {
          btn.textContent = left + 's';
        }
      }, 1000);
    };
    document.querySelectorAll('[data-login-mode]').forEach(btn => {
      btn.onclick = () => {
        state.loginMode = btn.dataset.loginMode === 'phone' ? 'phone' : 'uid';
        renderPage();
      };
    });
    if (regEl) {
      regEl.onchange = async () => {
        const val = regEl.value;
        if (val !== (state.boot?.settings?.serverRegion || 'cn')) {
          if (!state.boot) state.boot = {};
          if (!state.boot.settings) state.boot.settings = {};
          state.boot.settings.serverRegion = val;
          if (val === 'global') state.loginMode = 'uid';
          // 登录页切换区服是登录前的准备动作，不显示“已切换，请重新登录”的提示。
          const boot = await tryCall('settings.set', { serverRegion: val, announce: false });
          if (boot?.settings) state.boot = boot;
          renderPage();
        }
      };
    }
    // 原版只允许输入数字，这里保持一致，避免把非数字发给服务端。
    const digitsOnly = el => el && el.addEventListener('input', () => {
      const v = el.value.replace(/[^0-9]/g, '');
      if (el.value !== v) el.value = v;
    });
    digitsOnly(uidEl); digitsOnly(codeEl); digitsOnly(phoneEl); digitsOnly(phoneCodeEl);

    // UID 登录只接受 9-10 位游戏 UID。
    const uidValid = v => /^[0-9]{9,10}$/.test(v);
    if (send) {
      send.onclick = async () => {
        const uid = uidEl?.value.trim();
        if (!uid) return toast('请输入游戏 UID', 'err');
        if (!uidValid(uid)) return toast('请输入 9-10 位游戏 UID', 'err');
        try { await tryCall('login.sendCode', { uid }); toast('验证码已发送'); } catch { }
      };
    }
    if (go) {
      go.onclick = async () => {
        const uid = uidEl?.value.trim();
        const code = codeEl?.value.trim();
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
    }
    if (phoneSend) {
      phoneSend.onclick = async () => {
        const phone = phoneEl?.value.trim();
        if (!phone) return toast('请输入手机号', 'err');
        if (!/^1[3-9]\d{9}$/.test(phone)) return toast('请输入 11 位手机号', 'err');
        try {
          await tryCall('login.phone.sendCode', { phone });
          toast('短信验证码已发送');
          startCooldown(phoneSend, 60, '发送验证码');
        } catch { }
      };
    }
    if (phoneLogin) {
      phoneLogin.onclick = async () => {
        const phone = phoneEl?.value.trim();
        const code = phoneCodeEl?.value.trim();
        if (!phone) return toast('请输入手机号', 'err');
        if (!/^1[3-9]\d{9}$/.test(phone)) return toast('请输入 11 位手机号', 'err');
        if (!/^[0-9]{4,6}$/.test(code)) return toast('请输入 4-6 位短信验证码', 'err');
        try {
          await tryCall('login.phone.submit', { phone, code });
          toast('登录成功');
          state.boot = await call('boot');
          renderChrome();
          navigate('bag');
        } catch { }
      };
    }
    const tokenBtn = document.getElementById('loginTokenBtn');
    const tokenInput = document.getElementById('loginTokenInput');
    if (tokenBtn && tokenInput) {
      tokenBtn.onclick = async () => {
        const tok = tokenInput.value.trim();
        if (!tok) return toast('请输入 Token', 'err');
        try {
          await tryCall('login.token', { token: tok });
          toast('登录成功');
          state.boot = await call('boot');
          renderChrome();
          navigate('bag');
        } catch { }
      };
    }
    document.getElementById('loginInherit')?.addEventListener('click', showInheritLogin);
  }

  /* ---------------- 背包 ---------------- */
  async function ensureBag(force = false) {
    if (state.bag && !force) return state.bag;
    const r = await tryCall('bag.list');
    state.bag = r.items || [];
    updateSynthBadge();
    return state.bag;
  }

  const BAG_TABS = [['recent', '最近获得'], ['character_skin', '角色'], ['accessory', '配件'], ['emote', '表情']];
  const BAG_TAB_COUNT = k => k === 'recent' ? (state.bagRecentIds || []).length : 0;

  function assetMeta(item) {
    if (!item) return { role: '0', series: '' };
    const name = String(item.assetName || '').replace(/\.png$/i, '');
    let m;
    if ((item.type === 'character_skin' || !item.type) && (m = name.match(/^role_(\d+)(?:_(\d+))?/i))) {
      return { role: m[1], series: item.series || m[2] || '' };
    }
    if ((item.type === 'emote' || !item.type) && (m = name.match(/^emote_(\d+)_/i))) {
      return { role: m[1], series: '' };
    }
    if ((item.type === 'emote' || !item.type) && name.startsWith('emote_')) {
      const n = String(item.name || '');
      if (n.includes('阿布') || /^(?:angry|effort|happy|idle|pain|stunned|surprised|threaten|triumph)$/i.test(name.slice(6))) {
        return { role: '2000', series: '' };
      }
    }
    return { role: '0', series: item.series || '' };
  }
  function roleLabelOf(item) {
    const raw = String(item?.name || '').trim();
    if (!raw || /^role_\d+/i.test(raw) || /^emote_\d+/i.test(raw)) {
      const r = assetMeta(item).role;
      return (!r || r === '0') ? '通用' : '角色 ' + (item?.roleId || r);
    }
    const first = raw.split(/[・·]/)[0].trim();
    return first || (assetMeta(item).role === '0' ? '通用' : '角色 ' + (item?.roleId || assetMeta(item).role));
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
    const equippedAccessoryId = Number(state.boot?.appearance?.accessoryAssetId || 0);

    const seriesOptions = [...new Set(state.bag.filter(x => x.type === 'character_skin' && x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
    const roleMap = new Map();
    state.bag.filter(x => x.type === tab).forEach(x => {
      const r = assetMeta(x).role;
      if (!r || r === '0') return;
      const lbl = roleLabelOf(x);
      if (!roleMap.has(r) || (roleMap.get(r).startsWith('角色 ') && !lbl.startsWith('角色 '))) {
        roleMap.set(r, lbl || r);
      }
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
            ${tab === 'accessory' ? `<button class="btn ghost" id="bagRemoveAccessory" ${equippedAccessoryId ? '' : 'disabled'} title="从本地和云端摘除当前头饰">摘下头饰</button>` : ''}
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
      try {
        const r = await tryCall('appearance.sync');
        const blocked = [];
        if (r?.characterBlocked) blocked.push('角色');
        if (r?.accessoryBlocked) blocked.push('头饰');
        toast(blocked.length
          ? `已同步；未拥有的${blocked.join('、')}保留了云端内容`
          : '角色/配件已同步到云端（表情栏仅本地）');
      } catch { }
    };
    document.getElementById('bagRemoveAccessory')?.addEventListener('click', async () => {
      try {
        await tryCall('appearance.set', { type: 'accessory', id: 0 });
        const r = await tryCall('appearance.sync');
        state.boot = await call('boot');
        renderChrome();
        renderBag();
        toast(r?.characterBlocked ? '已摘下头饰；未拥有的角色保留了云端内容' : '已摘下头饰并同步云端');
      } catch { }
    });
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

  function isAccessoryEquipped(id) {
    return Number(state.boot?.appearance?.accessoryAssetId || 0) === Number(id);
  }

  function renderBagDetail() {
    const box = document.getElementById('bagDetail');
    if (!box) return;
    const x = state.bagSelected;
    if (!x) {
      box.innerHTML = `<div class="detail-empty">选择一件收集物<br><span style="font-size:13px">双击可直接装备；再次点击已佩戴的头饰可取消</span></div>`;
      return;
    }
    const accessoryEquipped = x.type === 'accessory' && isAccessoryEquipped(x.id);
    box.innerHTML = `
      <div class="detail-preview">${img(x.thumb, '', x.name)}</div>
      <div class="detail-name">${esc(x.name)}</div>
      <div class="detail-row"><span>类别</span><span>${TYPE_TEXT[x.type] || x.type}</span></div>
      <div class="detail-row"><span>稀有度</span><span>${RARITY_TEXT[effRarity(x.rarity)] || x.rarity}</span></div>
      <div class="detail-row"><span>系列</span><span>${esc(x.series || '—')}</span></div>
      <div class="detail-row"><span>数量</span><span>${x.quantity ?? 1}</span></div>
      <div class="spacer"></div>
      <button class="btn" id="equipBtn">${accessoryEquipped ? '取消头饰（本地显示）' : '装备（本地显示）'}</button>
      <button class="btn ghost" id="equipSyncBtn">${accessoryEquipped ? '取消头饰并同步云端' : '装备并同步云端'}</button>`;
    document.getElementById('equipBtn').onclick = () => equip(x, false);
    document.getElementById('equipSyncBtn').onclick = () => equip(x, true);
  }

  async function equip(item, sync = false) {
    if (!item) return;
    try {
      const unequipAccessory = item.type === 'accessory' && isAccessoryEquipped(item.id);
      await tryCall('appearance.set', { type: item.type, id: unequipAccessory ? 0 : item.id });
      let syncResult = null;
      if (sync) syncResult = await tryCall('appearance.sync');
      const blocked = [];
      if (syncResult?.characterBlocked) blocked.push('角色');
      if (syncResult?.accessoryBlocked) blocked.push('头饰');
      const base = unequipAccessory
        ? (sync ? '已取消头饰并同步云端' : '已取消头饰（本地显示）')
        : (sync ? '已装备并同步到云端' : '已装备到本地显示');
      toast(blocked.length ? `已更新；未拥有的${blocked.join('、')}保留了云端内容` : base);
      state.boot = await call('boot');
      renderChrome();
      if (state.page === 'bag') renderBagDetail();
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
      state.atlasAllCount = all.length;
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
    // 图鉴的“已拥有”状态来自背包；必须先等背包加载完，否则首屏会把全部条目判成未拥有。
    await ensureBag().catch(() => { });
    const ownedNames = new Set((state.bag || []).map(x => x.assetName));
    ensureEmotes().catch(() => { });
    const showSeries = type === 'character_skin';
    const showRole = type !== 'accessory';

    const seriesOptions = [...new Set(items.filter(x => x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
    const roleMap = new Map();
    if (showRole) {
      for (const x of items) {
        const r = assetMeta(x).role;
        if (!r || r === '0') continue;
        const lbl = roleLabelOf(x);
        if (!roleMap.has(r) || (roleMap.get(r).startsWith('角色 ') && !lbl.startsWith('角色 '))) {
          roleMap.set(r, lbl || r);
        }
      }
    }
    const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));

    setPage(`
      <div class="seg-tabs">
        ${ATLAS_TABS.map(([k, label]) => {
          const count = k === 'all' ? (state.atlasAllCount || '') : (state.atlas[k]?.length ?? '');
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
        <button class="pill ${state.atlasOwnedOnly ? 'active' : ''}" id="atlasOwnedOnly" title="只显示已经拥有的图鉴">仅拥有</button>
        <span class="spacer"></span>
        <span class="muted" id="atlasCount" style="font-size:13px">共 ${items.length} 件 · 已拥有会显示标记</span>
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
    document.getElementById('atlasOwnedOnly')?.addEventListener('click', e => {
      state.atlasOwnedOnly = !state.atlasOwnedOnly;
      e.currentTarget.classList.toggle('active', state.atlasOwnedOnly);
      renderAtlasList(items, ownedNames);
    });
    renderAtlasList(items, ownedNames);
  }

  function atlasFilteredItems(items, ownedNames) {
    let list = items;
    if (state.atlasSeries) list = list.filter(x => x.series === state.atlasSeries);
    if (state.atlasRole) list = list.filter(x => assetMeta(x).role === state.atlasRole);
    if (state.atlasRarity) list = list.filter(x => effRarity(x.rarity) === state.atlasRarity);
    if (state.atlasOwnedOnly) list = list.filter(x => ownedNames.has(x.assetName));
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
    const list = atlasFilteredItems(items, ownedNames);
    const count = document.getElementById('atlasCount');
    if (count) count.textContent = state.atlasOwnedOnly
      ? `仅拥有 ${list.length} 件`
      : `共 ${list.length} 件 · 已拥有会显示标记`;
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
        const accessoryEquipped = x.type === 'accessory' && isAccessoryEquipped(x.mainId);
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
              <button class="btn" id="mLocal">${accessoryEquipped ? '取消头饰（本地）' : '应用到本地'}</button>
              ${bagItem && x.type !== 'emote' ? `<button class="btn ghost" id="mEquip">${accessoryEquipped ? '取消头饰并同步云端' : '装备并同步云端'}</button>` : ''}
            </div>
          </div>`, h => {
          h.querySelector('#mClose').onclick = closeModal;
          h.querySelectorAll('[data-aslot]').forEach(el => el.onclick = () => {
            pickSlot = Number(el.dataset.aslot);
            h.querySelectorAll('[data-aslot]').forEach(e => e.classList.toggle('selected', e === el));
          });
          h.querySelector('#mLocal').onclick = async () => {
            try {
              if (accessoryEquipped && bagItem) {
                await equip(bagItem, false);
                closeModal();
                return;
              }
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

  function synthMaxRarityForCheck(type) {
    if ((state.atlas?.[type] || []).length) return synthMaxRarity(type);
    const order = { common: 1, uncommon: 2, rare: 3, epic: 4 };
    let max = 'common';
    for (const item of state.bag || []) {
      if (item.type !== type) continue;
      if ((order[effRarity(item.rarity)] || 0) > (order[max] || 0)) max = effRarity(item.rarity);
    }
    return max;
  }

  /** 按当前检查偏好判断是否有任何一组材料足够合成；不看当前页面筛选。 */
  function synthCanCraftAny() {
    const mode = state.boot?.settings?.synthCheckMode || 'up';
    if (mode === 'off' || !state.bag?.length) return false;
    const need = mode === 'same' ? 5 : 10;
    const groups = new Map();
    for (const item of state.bag) {
      if (!['character_skin', 'accessory', 'emote'].includes(item.type)) continue;
      const rarity = effRarity(item.rarity);
      if (mode === 'up' && rarity === synthMaxRarityForCheck(item.type)) continue;
      const usable = Math.max(0, (Number(item.quantity) || 1) - 1);
      if (usable <= 0) continue;
      const key = item.type + '|' + rarity;
      groups.set(key, (groups.get(key) || 0) + usable);
    }
    return [...groups.values()].some(count => count >= need);
  }

  function updateSynthBadge() {
    const ready = synthCanCraftAny();
    state.synthCanCraft = ready;
    const badge = document.querySelector('[data-tab-badge="synth"]');
    if (badge) badge.classList.toggle('show', ready);
    return ready;
  }

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
    const checkMode = state.boot?.settings?.synthCheckMode || 'up';
    const slotHtml = state.synthSlots.map((id, i) => {
      const item = id ? synthBagById(id) : null;
      return `<div class="synth-slot ${item ? 'filled' : ''}" data-slot="${i}" title="${item ? esc(item.name) : '空槽位'}">
        ${item ? img(item.thumb) : ''}<span class="idx">${i + 1}</span></div>`;
    }).join('');

    setPage(`
      <div class="synth-top">
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
        <div class="synth-check-card">
          <div class="setting-title">合成提醒</div>
          <div class="setting-sub">获得新物品后检查是否可合成</div>
          <div class="pill-row" style="margin-top:8px">
            <button class="pill ${checkMode === 'up' ? 'active' : ''}" data-scm="up">升阶</button>
            <button class="pill ${checkMode === 'same' ? 'active' : ''}" data-scm="same">平阶</button>
            <button class="pill ${checkMode === 'off' ? 'active' : ''}" data-scm="off">关闭</button>
          </div>
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
    document.querySelectorAll('[data-scm]').forEach(b => b.onclick = async () => {
      const mode = b.dataset.scm;
      try {
        const boot = await tryCall('settings.set', { synthCheckMode: mode });
        if (boot?.settings) state.boot = boot;
        updateSynthBadge();
        renderSynth();
        toast(mode === 'off' ? '已关闭合成提醒' : (mode === 'same' ? '合成提醒改为平阶检查' : '合成提醒改为升阶检查'));
      } catch { }
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
    updateSynthBadge();
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
      updateSynthBadge();
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

  function bagDelta(before, after) {
    const oldQty = new Map((before || []).map(x => [String(x.id), Number(x.quantity) || 1]));
    const gained = [];
    for (const item of after || []) {
      const qty = Number(item.quantity) || 1;
      const diff = qty - (oldQty.get(String(item.id)) || 0);
      if (diff > 0) gained.push({ ...item, gained: diff });
    }
    return gained;
  }

  function directRedeemItems(result) {
    const candidates = [result?.items, result?.data?.items, result?.assets, result?.data];
    for (const candidate of candidates) {
      if (!Array.isArray(candidate)) continue;
      return candidate.map(x => {
        const id = Number(x.asset_id || x.id || 0);
        const bagItem = (state.bag || []).find(v => v.id === id);
        return {
          ...(bagItem || {}),
          ...x,
          id: id || bagItem?.id || 0,
          name: x.name || x.asset_name || bagItem?.name || '新物品',
          thumb: x.thumb || bagItem?.thumb || '',
          gained: Number(x.quantity || x.count || 1) || 1,
        };
      }).filter(x => x.id > 0 || x.name);
    }
    return [];
  }

  function showRedeemReward(items, code) {
    const list = (items || []).filter(Boolean);
    const body = list.length
      ? `<div class="redeem-items">${list.map(x => `
          <div class="redeem-item">
            ${x.thumb ? img(x.thumb, 'redeem-thumb', x.name) : '<div class="redeem-thumb">🎁</div>'}
            <div class="redeem-name">${esc(x.name || '新物品')}</div>
            <div class="redeem-qty">×${x.gained || 1}</div>
          </div>`).join('')}</div>`
      : '<div class="r-sub">奖励已发放到背包，请查看背包。</div>';
    modal(`<div class="reward">
      <div class="r-name">兑换成功</div>
      <div class="r-sub">${esc(code)}</div>
      ${body}
      <button class="btn" id="mOk" style="margin-top:8px">收下</button>
    </div>`, host => host.querySelector('#mOk').onclick = closeModal);
  }

  async function redeemCode(code) {
    // 先取一份兑换前背包，用于兼容官方接口不返回奖励明细的情况。
    let before = [];
    let beforeOk = false;
    try {
      const r = await call('bag.list');
      before = r.items || [];
      state.bag = before;
      beforeOk = true;
    } catch { }

    const result = await tryCall('redeem', { code });
    let gained = [];
    if (beforeOk) {
      for (let attempt = 0; attempt < 10; attempt++) {
        try {
          const r = await call('bag.list');
          state.bag = r.items || [];
          gained = bagDelta(before, state.bag);
          updateSynthBadge();
          if (gained.length) break;
        } catch { }
        await new Promise(resolve => setTimeout(resolve, 300));
      }
    }
    if (!beforeOk) {
      try {
        const r = await call('bag.list');
        state.bag = r.items || [];
        updateSynthBadge();
      } catch { }
    }
    if (!gained.length) {
      const direct = directRedeemItems(result);
      if (direct.length) gained = direct;
    }
    state.bagNewReady = false;
    refreshNewAssets();
    if (state.page === 'bag') renderBag();
    if (gained.length) showRedeemReward(gained, code);
    else toast('兑换成功，奖励已发放到背包');
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
    return `${room.state}|${room.roomNo || ''}|${room.connected ? 1 : 0}|${room.serviceUnavailable ? 1 : 0}|${room.hiddenAll ? 1 : 0}|${(room.history || []).join(',')}|${ms}`;
  }

  function renderRoom() {
    const room = state.boot?.room || { state: 'idle', members: [], history: [] };
    state.roomSig = roomSignature(room);
    const joined = room.state === 'joined';
    const serviceDown = !!room.serviceUnavailable;
    const auth = state.boot?.auth || {};
    const emotes = state.boot?.appearance?.emoteAssetIds || [];
    setPage(`
      <div class="room-layout">
        <div class="room-left">
          ${serviceDown ? `
          <div class="panel-box" style="background:#fff0ee;border:1px solid #f2b8ad;color:#9b3b2e;font-size:13px;line-height:1.6">
            联机服务暂不可用：官方联机服务器暂时无法连接，正在自动重试。
          </div>` : ''}
          <div class="panel-box">
            <div class="detail-row" style="margin-bottom:6px"><span>当前房间</span><span>${serviceDown ? '联机服务暂不可用' : (joined ? (room.connected ? '已连接' : '连接中…（自动重试）') : '未加入')}</span></div>
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
        </div>
        <div class="room-right">
          <div class="panel-box" style="flex:1;display:flex;flex-direction:column;min-height:0">
            <div style="display:flex;align-items:center;gap:10px">
              <div class="section-title" style="margin-left:0;flex:1">房间成员（${room.members?.length || 0}）</div>
              <div class="layout-switch" id="catLayout" style="margin-left:auto" title="切换排列方向并重新排序">
                <button type="button" class="layout-choice ${(state.boot?.settings?.catLayout || 'horizontal') !== 'vertical' ? 'active' : ''}" data-layout="horizontal">横向排列</button>
                <button type="button" class="layout-choice ${(state.boot?.settings?.catLayout || 'horizontal') === 'vertical' ? 'active' : ''}" data-layout="vertical">纵向排列</button>
              </div>
            </div>
            <div class="member-list" id="memberList">
              ${(room.members || []).map(m => `
                <div class="member ${m.owner ? 'owner' : ''}">
                  <span class="dot ${m.online ? '' : 'off'}"></span>
                  <span class="m-name">${esc(m.nickname || '漂泊者')}</span>
                  <span class="m-actions">
                    ${m.owner ? '<span class="m-role">房主</span>' : ''}
                    ${m.self ? '<span class="m-role">我</span>' : ''}
                    ${!m.online ? '<span class="m-role">离线</span>' : ''}
                    ${!m.self ? (room.hiddenAll
                      ? '<span class="m-role" title="已开启“隐藏其他成员桌宠”">已隐藏</span>'
                      : `<button class="chip" style="padding:2px 10px" data-hide="${m.id}" data-visible="${m.visible ? 1 : 0}">${m.visible ? '隐藏' : '显示'}</button>`) : ''}
                  </span>
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
    document.querySelectorAll('#catLayout [data-layout]').forEach(btn => {
      btn.onclick = async () => {
        const val = btn.dataset.layout === 'vertical' ? 'vertical' : 'horizontal';
        try {
          await tryCall('settings.set', { catLayout: val });
          if (state.boot?.settings) state.boot.settings.catLayout = val;
          await tryCall('room.resetOrder');
          renderRoom();
          toast(val === 'vertical' ? '已切换为纵向排列' : '已切换为横向排列');
        } catch { }
      };
    });
    document.getElementById('hideOthers').onclick = async () => {
      const hide = !state.boot?.settings?.hideOtherPets;
      try {
        const r = await tryCall('room.hideOthers', { hide });
        if (state.boot) state.boot.settings.hideOtherPets = r?.hiddenAll ?? hide;
        state.boot.room = r; renderRoom();
      } catch { }
    };
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

  function bindChestButtons() {
    document.querySelectorAll('[data-chest]').forEach(b => b.onclick = async () => {
      try {
        const r = await tryCall('chest.open', { place: b.dataset.chest, clickNum: 1000 });
        const id = Number(r?.asset_id) || 0;
        await refreshAfterAcquire(id);
        let thumb = '', name = '领取成功';
        if (id) {
          await ensureCatalog('character_skin').catch(() => []);
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
  function releaseNotesHtml(markdown) {
    const inline = s => esc(s)
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    const lines = String(markdown || '').split(/\r?\n/);
    const out = [];
    let inList = false;
    let skipDownload = false;
    const closeList = () => { if (inList) { out.push('</ul>'); inList = false; } };
    for (const raw of lines) {
      const line = raw.trim();
      if (!line) { closeList(); continue; }
      let m;
      if ((m = line.match(/^#{1,4}\s+(.*)$/))) {
        // 下载说明由横幅底部统一提供，多个版本拼接时不要重复整段。
        if (/下载说明|download/i.test(m[1])) { skipDownload = true; closeList(); continue; }
        skipDownload = false;
        closeList();
        out.push(`<div class="update-h">${inline(m[1])}</div>`);
      } else if (skipDownload) {
        continue;
      } else if ((m = line.match(/^[-*]\s+(.*)$/))) {
        if (!inList) { out.push('<ul>'); inList = true; }
        out.push(`<li>${inline(m[1])}</li>`);
      } else {
        closeList();
        out.push(`<p>${inline(line)}</p>`);
      }
    }
    closeList();
    return out.join('');
  }

  function compareVersions(left, right) {
    const a = String(left || '').replace(/^v/i, '').split(/[.+-]/).map(x => parseInt(x, 10) || 0);
    const b = String(right || '').replace(/^v/i, '').split(/[.+-]/).map(x => parseInt(x, 10) || 0);
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const av = i < a.length ? a[i] : 0;
      const bv = i < b.length ? b[i] : 0;
      if (av !== bv) return av < bv ? -1 : 1;
    }
    return 0;
  }

  /// 官方版本提醒固定在内容区顶部；公告只在设置页显示，避免打扰其他功能。
  function renderNotices() {
    const host = document.getElementById('noticeHost');
    if (!host) return;

    const oc = state.compatibility?.officialCheck;
    let officialHtml = '';
    let officialKey = '';
    if (oc && (oc.state === 'unverified' || oc.state === 'forced')) {
      officialKey = `wn_official_notice_${oc.state}_${oc.officialVersion || ''}`;
      let dismissed = false;
      try { dismissed = localStorage.getItem(officialKey) === '1'; } catch { }
      if (!dismissed) {
        const danger = oc.state === 'forced';
        officialHtml = `<section class="official-notice ${danger ? 'danger' : 'warning'}">
          <div class="official-notice-main">
            <div class="official-notice-icon">${danger ? '!' : '?'}</div>
            <div class="official-notice-copy">
              <div class="official-notice-title">官方客户端已${danger ? '强制' : ''}更新至 ${esc(oc.officialVersion || '新版本')}</div>
              <div class="official-notice-text">${esc(oc.message || '')}</div>
              <div class="official-notice-meta">当前 Native 验证基线：${esc(oc.testedVersion || '—')}${oc.stale ? ' · 显示上次检查结果' : ''}</div>
            </div>
            <button class="official-notice-close" data-official-dismiss title="关闭">×</button>
          </div>
          <div class="official-notice-actions">
            <a class="btn ghost" href="https://github.com/DanielToyama/WuwaTappoNative/releases/latest" target="_blank" rel="noreferrer">查看项目状态</a>
            <button class="btn ghost" data-official-dismiss>${danger ? '暂时继续（联机可能不可用）' : '继续使用'}</button>
          </div>
        </section>`;
      }
    }

    const announcementHtml = state.page === 'settings' ? announcementCardHtml() : '';
    host.innerHTML = officialHtml + announcementHtml;
    if (officialHtml) {
      host.querySelectorAll('[data-official-dismiss]').forEach(el => {
        el.onclick = () => {
          try { localStorage.setItem(officialKey, '1'); } catch { }
          renderNotices();
        };
      });
    }
    bindAnnouncement(host);
  }

  function announcementCardHtml() {
    const list = Array.isArray(state.announcements) ? state.announcements : [];
    if (!list.length) return '';
    const a = list[0];
    const severity = ['info', 'warning', 'critical'].includes(String(a.severity || '').toLowerCase())
      ? String(a.severity).toLowerCase() : 'info';
    const meta = ({
      info: { label: '公告', icon: 'i' },
      warning: { label: '注意', icon: '!' },
      critical: { label: '重要', icon: '!' }
    })[severity];
    const count = list.length > 1 ? `<span class="announcement-count">1 / ${list.length}</span>` : '';
    const dateValue = a.publishedAt ? new Date(a.publishedAt) : null;
    const dateText = dateValue && !Number.isNaN(dateValue.getTime()) ? dateValue.toLocaleDateString('zh-CN') : '';
    const action = a.action && a.action.url
      ? `<a class="btn ghost announcement-action" href="${esc(a.action.url)}" target="_blank" rel="noreferrer" data-ann-action>${esc(a.action.label || '查看详情')}</a>`
      : '';
    const canClose = a.dismissible !== false;
    const close = canClose
      ? '<button class="announcement-close" data-ann-close title="关闭">×</button>'
      : '';
    const confirm = canClose ? '' : '<button class="btn ghost" data-ann-close>我知道了</button>';
    return `<section class="announcement-card ${severity}" data-ann-id="${esc(a.id)}">
      <div class="announcement-head">
        <span class="announcement-badge"><span>${meta.icon}</span>${meta.label}</span>
        <span class="announcement-title">${esc(a.title || '公告')}</span>
        ${count}
        ${close}
      </div>
      <div class="announcement-body">${esc(a.body || '')}</div>
      <button class="announcement-expand hidden" data-ann-expand>展开全文</button>
      <div class="announcement-foot">
        <span class="announcement-meta">${dateText ? esc(dateText) + ' · ' : ''}Tappo 公告</span>
        <span class="announcement-actions">${action}${confirm}</span>
      </div>
    </section>`;
  }

  function bindAnnouncement(host) {
    const card = host.querySelector('.announcement-card');
    if (!card) return;
    const body = card.querySelector('.announcement-body');
    const expand = card.querySelector('[data-ann-expand]');
    if (body && expand) {
      if (body.scrollHeight > body.clientHeight + 2) expand.classList.remove('hidden');
      expand.onclick = () => {
        const expanded = body.classList.toggle('expanded');
        expand.textContent = expanded ? '收起' : '展开全文';
      };
    }
    const id = card.dataset.annId || '';
    card.querySelectorAll('[data-ann-close]').forEach(el => {
      el.onclick = () => { void dismissAnnouncement(id); };
    });
    card.querySelectorAll('[data-ann-action]').forEach(el => {
      el.addEventListener('click', () => { void dismissAnnouncement(id); });
    });
  }

  let announcementBusy = false;
  async function dismissAnnouncement(id) {
    if (!id || announcementBusy) return;
    announcementBusy = true;
    try {
      const remaining = await call('announcement.read', { id });
      state.announcements = Array.isArray(remaining)
        ? remaining
        : (state.announcements || []).filter(a => a.id !== id);
      renderNotices();
    } catch { }
    finally { announcementBusy = false; }
  }

  function compatibilityBannerHtml() {
    const c = state.compatibility;
    if (!c) return '';
    const native = c.native || {};
    const officialCheck = c.officialCheck;
    const currentNative = state.boot?.version || '';
    const belowMinSafe = !!(native.minSafe && currentNative && compareVersions(currentNative, native.minSafe) < 0);
    let status = String(c.status || 'unknown').toLowerCase();
    if (status === 'safe' && (belowMinSafe || officialCheck?.state === 'unverified' || officialCheck?.state === 'forced'))
      status = officialCheck?.state === 'forced' ? 'unsafe' : 'warning';
    const meta = ({
      safe: { cls: 'safe', icon: '✓', label: '安全', title: '当前版本可继续使用' },
      warning: { cls: 'warning', icon: '!', label: '注意', title: '当前版本需要关注' },
      unsafe: { cls: 'unsafe', icon: '×', label: '不安全', title: '当前版本不建议继续联网' },
      maintenance: { cls: 'maintenance', icon: '…', label: '维护中', title: '官方服务可能正在维护' },
      unknown: { cls: 'unknown', icon: '?', label: '未知', title: '暂时无法确认版本状态' }
    })[status] || { cls: 'unknown', icon: '?', label: '未知', title: '暂时无法确认版本状态' };
    const official = c.official || {};
    const cn = official.cn || {};
    const global = official.global || {};
    const globalUsesCn = global.inheritCn === true && !global.latest;
    const globalLatest = global.latest || (globalUsesCn ? cn.latest : '');
    const protocol = c.protocol || {};
    const kill = c.killSwitch || {};
    const force = !!kill.forceOffline;
    const updated = c.updatedAt ? new Date(c.updatedAt) : null;
    const updatedText = updated && !Number.isNaN(updated.getTime())
      ? updated.toLocaleString('zh-CN', { hour12: false })
      : '';
    const bits = [];
    if (native.latest) bits.push(`最新 ${esc(native.latest)}`);
    if (native.minSafe) bits.push(`最低兼容 ${esc(native.minSafe)}`);
    if (cn.latest) bits.push(`官方 CN ${esc(cn.latest)}`);
    if (globalLatest) bits.push(`官方 Global ${esc(globalLatest)}${globalUsesCn ? '（沿用 CN）' : ''}`);
    if (protocol.testedOfficial) bits.push(`验证基线 ${esc(protocol.testedOfficial)}`);
    if (officialCheck?.officialVersion) {
      const label = ({ verified: '已验证', unverified: '未验证', forced: '强制更新', unavailable: '检查失败' })[officialCheck.state] || '未知';
      bits.push(`官方检查 ${esc(officialCheck.officialVersion)}（${label}）`);
    }
    if (updatedText) bits.push(`更新于 ${esc(updatedText)}`);
    return `<section class="compat-banner ${meta.cls}">
      <div class="compat-head">
        <span class="compat-badge"><span>${meta.icon}</span>${meta.label}</span>
        <span class="compat-title">版本状态：${meta.title}</span>
        <a class="btn ghost compat-link" href="https://code.danieluu.top/WuwaTappoNative/" target="_blank" rel="noreferrer">状态页</a>
      </div>
      <div class="compat-sub">${esc(c.message || '暂无说明')}</div>
      <div class="compat-meta">${bits.join(' · ')}</div>
      ${belowMinSafe ? `<div class="compat-force">当前 Native ${esc(currentNative)} 低于最低兼容版本 ${esc(native.minSafe)}，不保证兼容当前官方服务，请更新后再使用联机。</div>` : ''}
      ${force ? `<div class="compat-force">安全模式：${esc(kill.reason || '维护者要求暂停联网')}</div>` : ''}
      ${protocol.breaking ? '<div class="compat-force">检测到破坏性协议变更，请尽快更新。</div>' : ''}
    </section>`;
  }

  function updateBannerHtml() {
    const u = state.update;
    if (!u || state.updateDismissed) return '';
    // 兼容旧序列化：0.0.25 曾把 ReleaseInfo 的 PascalCase 属性直接推给前端，导致标题/内容为空。
    const tag = u.tag || u.Tag || u.name || u.Name || '新版本';
    const body = u.body || u.Body || '';
    const dateValue = u.publishedAt || u.PublishedAt;
    const date = dateValue ? new Date(dateValue).toLocaleDateString('zh-CN') : '';
    const entries = Array.isArray(u.entries) ? u.entries : (Array.isArray(u.Entries) ? u.Entries : []);
    const multi = entries.length > 1;
    const notes = multi
      ? entries.map(e => {
          const etag = e.tag || e.Tag || '';
          const edate = e.publishedAt || e.PublishedAt;
          const edateText = edate ? new Date(edate).toLocaleDateString('zh-CN') : '';
          return `<section class="update-entry">
            <div class="update-entry-head"><span>${esc(etag)}</span>${edateText ? `<span class="update-entry-date">${esc(edateText)}</span>` : ''}</div>
            ${releaseNotesHtml(e.body || e.Body || '')}
          </section>`;
        }).join('')
      : releaseNotesHtml(body);
    return `<section class="update-banner">
      <div class="update-banner-head">
        <div>
          <div class="update-banner-title">发现新版本 ${esc(tag)}</div>
          <div class="update-banner-sub">当前版本 ${esc(state.boot?.version || '')}${date ? ' · ' + esc(date) : ''}${multi ? ` · 包含 ${entries.length} 个版本更新` : ''}</div>
        </div>
        <button class="update-dismiss" id="updateDismiss" title="本次启动不再显示">×</button>
      </div>
      <div class="update-notes">${notes}</div>
      <div class="update-banner-actions">
        <a class="btn" href="${esc(u.url || u.Url || 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest')}" target="_blank" rel="noreferrer">前往 GitHub 更新</a>
        <span class="update-qq">交流 QQ 群：<strong>1130030892</strong>，群文件也可下载</span>
      </div>
    </section>`;
  }

  async function loadAutoEmoteOptions() {
    const sel = document.getElementById('autoEmoteId');
    if (!sel) return;
    try {
      const list = await ensureEmotes();
      if (!sel.isConnected) return;
      const current = String(state.boot?.settings?.autoEmoteId || 0);
      const options = [...(list || [])]
        .filter(x => Number(x.mainId) > 0)
        .sort((a, b) => String(a.name || '').localeCompare(String(b.name || ''), 'zh'));
      sel.innerHTML = '<option value="0">请选择表情</option>' +
        options.map(x => `<option value="${x.mainId}">${esc(x.name || x.mainId)}</option>`).join('');
      sel.value = current;
      if (sel.value !== current) sel.value = '0';
    } catch { }
  }

  function renderSettings() {
    const s = state.boot?.settings || {};
    const auth = state.boot?.auth || {};
    const acc = state.boot?.appearance || {};
    const accId = Number(acc.accessoryAssetId || 0);
    const rhythmCount = Math.max(4, Math.min(6, Number(s.rhythmKeyCount) || 4));
    const rhythmDefaults = { 4: ['D', 'F', 'J', 'K'], 5: ['D', 'F', 'J', 'K', 'L'], 6: ['S', 'D', 'F', 'J', 'K', 'L'] };
    const rhythmKeys = Array.isArray(s.rhythmKeys) ? s.rhythmKeys : rhythmDefaults[rhythmCount];
    const rhythmDisplay = (key) => key === 'SPACE' ? '␣' : (key || '');
    const scalePct = Math.max(25, Math.min(200, Math.round((Number(s.scale) || 1) * 100)));
    const scalePreset = [25, 50, 75, 100, 125, 150, 200].includes(scalePct);
    const remoteFollowMain = s.remoteFollowMain !== false;
    const remoteCustomPct = Math.max(25, Math.min(200, Math.round((Number(s.remoteScale) || 1) * 100)));
    const remoteScalePct = remoteFollowMain ? scalePct : remoteCustomPct;
    const remoteScalePreset = [25, 50, 75, 100, 125, 150, 200].includes(remoteScalePct);
    const opacityPct = Math.max(20, Math.min(100, Math.round((Number(s.opacity) || 1) * 100)));
    const remoteFollowMainOpacity = s.remoteFollowMainOpacity !== false;
    const remoteCustomOpacityPct = Math.max(20, Math.min(100, Math.round((Number(s.remoteOpacity) || 1) * 100)));
    const remoteOpacityPct = remoteFollowMainOpacity ? opacityPct : remoteCustomOpacityPct;
    const remoteNameScalePct = Math.max(50, Math.min(200, Math.round((Number(s.remoteNameScale) || 1) * 100)));
    const accX = Number(acc.accessoryOffsetX || 0);
    const accY = Number(acc.accessoryOffsetY || 0);
    // 快捷键/菜单改设置会触发 boot 重推；保留滚动位置，别把用户顶回顶部。
    const prevScroll = document.querySelector('.settings-scroll')?.scrollTop || 0;
    setPage(`
      <div class="settings-scroll">
        ${updateBannerHtml()}
        <div class="section">
          <div class="section-title">账号</div>
          <div class="panel-box" style="display:flex;flex-direction:column;gap:12px">
            <div class="setting-item" style="padding:0 0 10px 0;border-bottom:1px solid var(--border,#eee)">
              <div><div class="setting-title">服务器区服</div><div class="setting-sub">选择官方桌宠所在的区服（切换后需重新登录）</div></div>
              <div class="setting-content">
                <select class="select" id="setServerRegion">
                  <option value="cn" ${(s.serverRegion || 'cn') === 'cn' ? 'selected' : ''}>国服（CN）</option>
                  <option value="global" ${(s.serverRegion || 'cn') === 'global' ? 'selected' : ''}>国际服（Global）</option>
                </select>
              </div>
            </div>
            ${isLogged() ? `
              <div class="detail-row"><span>昵称</span><span>${esc(auth.nickname || '—')}</span></div>
              <div class="detail-row"><span>UID</span><span>${auth.uid || '—'}</span></div>
              <div class="detail-row"><span>可用点击</span><span data-click-avail>${auth.clicks || 0}</span></div>
              <div class="detail-row"><span>累计点击</span><span class="click-total"><span data-click-total>${auth.clickTotal ?? auth.clicks ?? 0}</span><button class="btn ghost click-boost" id="boostClicks" title="本地增加 10000 次点击并立即上报；连续操作间隔至少 0.5 秒">+10000</button></span></div>
              <div class="detail-row"><span>已消耗</span><span data-click-consume>${auth.clickConsume || 0}</span></div>
              <div style="display:flex;gap:10px">
                <button class="btn ghost" id="userRefresh">刷新账号信息</button>
                <button class="btn danger" id="logoutBtn">退出登录</button>
              </div>` : ((s.serverRegion || 'cn') === 'global' ? `
              <div class="detail-row"><span>登录</span><span class="muted">国际服请使用下方继承官方登录状态，或导入 Token</span></div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setToken" placeholder="粘贴官方客户端 infoToken" style="flex:1" />
                <button class="btn ghost" id="setTokenBtn">导入 token</button>
              </div>` : `
              <div class="detail-row"><span>登录</span><span class="muted">UID / 手机号 + 验证码（也可继承官方登录状态）</span></div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setUid" placeholder="游戏 UID（9-10 位）" style="width:180px" />
                <input class="input" id="setCode" placeholder="6 位验证码" style="width:140px" />
                <button class="btn ghost" id="setSend">发送验证码</button>
                <button class="btn" id="setLogin">登录</button>
              </div>
              <div style="display:flex;gap:10px;align-items:center">
                <button class="btn ghost" id="setPhoneLoginOpen">手机号登录</button>
                <span class="muted" style="font-size:12px">库洛通行证手机号 + 短信验证码</span>
              </div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setToken" placeholder="粘贴官方客户端 infoToken" style="flex:1" />
                <button class="btn ghost" id="setTokenBtn">导入 token</button>
              </div>`)}
            <div class="inherit-login">
              <div class="setting-sub">建议在获取前先关闭本家程序的开机自动启动，以免下次开机因为 token 冲突导致掉线。</div>
              <button class="btn ghost" id="inheritLogin">继承官方程序登录状态</button>
            </div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">窗口与桌宠</div>
          <div class="setting-item">
            <div><div class="setting-title">桌宠大小</div><div class="setting-sub">拖动滑条以 1% 细调，或选择常用档位；缩放以猫底部中心为锚点</div></div>
            <div class="setting-content">
              <select class="select" id="setScale" style="width:96px">
                ${[0.25, 0.5, 0.75, 1, 1.25, 1.5, 2].map(v => `<option value="${v}" ${Math.abs((s.scale || 1) - v) < 0.005 ? 'selected' : ''}>${Math.round(v * 100)}%</option>`).join('')}
                <option value="custom" disabled ${scalePreset ? '' : 'selected'}>自定义</option>
              </select>
              <input type="range" min="25" max="200" step="1" value="${scalePct}" id="setScaleRange" style="width:180px" />
              <span id="scaleVal">${scalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">联机桌宠大小</div><div class="setting-sub">默认跟随主角；取消勾选后可单独调整所有联机角色</div></div>
            <div class="setting-content">
              <label style="display:flex;align-items:center;gap:6px;font-size:13px;color:var(--ink);cursor:pointer;white-space:nowrap">
                <input type="checkbox" id="remoteFollowMain" ${remoteFollowMain ? 'checked' : ''} style="width:16px;height:16px;accent-color:#e3a765" />
                跟随主角
              </label>
              <select class="select" id="setRemoteScale" style="width:96px" ${remoteFollowMain ? 'disabled' : ''}>
                ${[0.25, 0.5, 0.75, 1, 1.25, 1.5, 2].map(v => `<option value="${v}" ${Math.abs(remoteScalePct / 100 - v) < 0.005 ? 'selected' : ''}>${Math.round(v * 100)}%</option>`).join('')}
                <option value="custom" disabled ${remoteScalePreset ? '' : 'selected'}>自定义</option>
              </select>
              <input type="range" min="25" max="200" step="1" value="${remoteScalePct}" id="setRemoteScaleRange" style="width:150px" ${remoteFollowMain ? 'disabled' : ''} />
              <span id="remoteScaleVal">${remoteScalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">桌宠不透明度</div><div class="setting-sub">100% 为完全不透明</div></div>
            <div class="setting-content">
              <input type="range" min="20" max="100" value="${opacityPct}" id="setOpacity" style="width:180px" />
              <span id="opacityVal">${opacityPct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">联机桌宠不透明度</div><div class="setting-sub">默认跟随主角；取消勾选后可单独调整所有联机角色</div></div>
            <div class="setting-content">
              <label style="display:flex;align-items:center;gap:6px;font-size:13px;color:var(--ink);cursor:pointer;white-space:nowrap">
                <input type="checkbox" id="remoteFollowMainOpacity" ${remoteFollowMainOpacity ? 'checked' : ''} style="width:16px;height:16px;accent-color:#e3a765" />
                跟随主角
              </label>
              <input type="range" min="20" max="100" step="1" value="${remoteOpacityPct}" id="setRemoteOpacityRange" style="width:180px" ${remoteFollowMainOpacity ? 'disabled' : ''} />
              <span id="remoteOpacityVal">${remoteOpacityPct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">联机玩家名字大小</div><div class="setting-sub">相对默认字号 50%–200%，只影响联机成员昵称</div></div>
            <div class="setting-content">
              <input type="range" min="50" max="200" step="1" value="${remoteNameScalePct}" id="setRemoteNameScale" style="width:180px" />
              <span id="remoteNameScaleVal">${remoteNameScalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">按键气泡</div><div class="setting-sub">按键时在桌宠上方冒出键帽，默认开启</div></div>
            <div class="switch ${s.keyBubbles !== false ? 'on' : ''}" data-switch="keyBubbles"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">动画帧率</div><div class="setting-sub">表情和按键气泡的刷新帧率，默认 60；帧率越低占用越少</div></div>
            <div class="setting-content">
              <div class="layout-switch" id="setAnimationFps">
                ${[30, 60, 120].map(fps => `<button type="button" class="layout-choice ${(s.animationFps || 60) === fps ? 'active' : ''}" data-fps="${fps}">${fps} 帧</button>`).join('')}
              </div>
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
            <div><div class="setting-title">悬停隐藏程度</div><div class="setting-sub">鼠标停留后降到正常不透明度的百分比，默认 40%</div></div>
            <div class="setting-content">
              <input type="range" min="10" max="100" value="${Math.round((s.hoverFadeOpacity ?? 0.4) * 100)}" id="setHoverOpacity" style="width:180px" />
              <span id="hoverOpacityVal">${Math.round((s.hoverFadeOpacity ?? 0.4) * 100)}%</span>
            </div>
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
          <div class="section-title">头饰位置微调</div>
          <div class="setting-item">
            <div><div class="setting-title">当前头饰</div><div class="setting-sub">${accId ? '按方向微调当前头饰相对角色的位置，每个头饰独立保存' : '当前未佩戴头饰，先在背包 → 配件中选择一件'}</div></div>
            <div class="setting-content" style="align-items:center">
              <div class="nudge-pad">
                <button class="btn ghost nudge-btn" data-nudge="0,-1" ${accId ? '' : 'disabled'} title="上移">↑</button>
                <button class="btn ghost nudge-btn" data-nudge="-1,0" ${accId ? '' : 'disabled'} title="左移">←</button>
                <button class="btn ghost nudge-btn" data-nudge="0,1" ${accId ? '' : 'disabled'} title="下移">↓</button>
                <button class="btn ghost nudge-btn" data-nudge="1,0" ${accId ? '' : 'disabled'} title="右移">→</button>
              </div>
              <span class="muted" style="font-size:12px">X ${accX} · Y ${accY}</span>
              <button class="btn ghost" id="resetAccessoryOffset" ${accId && (accX || accY) ? '' : 'disabled'}>归零</button>
            </div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">桌面键盘</div>
          <div class="setting-item">
            <div><div class="setting-title">音游模式</div><div class="setting-sub">把鸣潮专属指示器替换音游指示器；开启后立即切换</div></div>
            <div class="switch ${s.rhythmMode ? 'on' : ''}" data-switch="rhythmMode"></div>
          </div>
          ${s.rhythmMode ? `<div class="setting-item">
            <div><div class="setting-title">音游键数</div><div class="setting-sub">选择 4K、5K 或 6K 键位布局</div></div>
            <div class="setting-content">
              <select class="select" id="setRhythmKeyCount">
                ${[4, 5, 6].map(n => `<option value="${n}" ${rhythmCount === n ? 'selected' : ''}>${n}K</option>`).join('')}
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">音游触发键位</div><div class="setting-sub">从左到右 ${rhythmCount} 个键；支持字母、数字、空格和 ; ' , . / 等符号；空格显示为横过来的 [</div></div>
            <div class="setting-content">
              ${Array.from({ length: rhythmCount }, (_, i) => `<input class="input" data-rhythm-key="${i}" maxlength="1" value="${esc(rhythmDisplay(rhythmKeys[i] || rhythmDefaults[rhythmCount][i]))}" style="width:42px;text-align:center;text-transform:uppercase;font-weight:700" inputmode="text" autocomplete="off" />`).join('')}
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
            <div><div class="setting-title">联机桌宠排列</div><div class="setting-sub">横向贴底部排成一排；纵向贴屏幕右侧，排满后向左换列</div></div>
            <div class="setting-content">
              <div class="layout-switch" id="setCatLayout">
                <button type="button" class="layout-choice ${(s.catLayout || 'horizontal') !== 'vertical' ? 'active' : ''}" data-layout="horizontal">横向排列</button>
                <button type="button" class="layout-choice ${(s.catLayout || 'horizontal') === 'vertical' ? 'active' : ''}" data-layout="vertical">纵向排列</button>
              </div>
            </div>
          </div>
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
          <div class="setting-item">
            <div><div class="setting-title">手动开启宝箱</div><div class="setting-sub">每次消耗 1000 点击数，冷却 30 分钟，以服务器返回为准</div></div>
            <div class="setting-content">
              <button class="btn ghost" data-chest="left">开启左宝箱</button>
              <button class="btn ghost" data-chest="right">开启右宝箱</button>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">宝箱冷却</div><div class="setting-sub" id="chestStatus">正在读取…</div></div>
          </div>
        </div>

        <div class="section">
          <div class="section-title">自动化</div>
          <div class="setting-item">
            <div><div class="setting-title">自动表情</div><div class="setting-sub">每次产生敲击时自动发送一个表情；受下方每秒上限限制</div></div>
            <div class="switch ${s.autoEmoteEnabled ? 'on' : ''}" data-switch="autoEmoteEnabled"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">自动表情频率上限</div><div class="setting-sub">滚动一秒内最多发送数量，范围 1–20</div></div>
            <div class="setting-content"><input class="input" id="autoEmoteRate" type="number" min="1" max="20" step="1" value="${s.autoEmoteMaxPerSecond ?? 20}" style="width:86px" /><span class="muted">个/秒</span></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">表情来源</div><div class="setting-sub">快捷栏随机：只从自己的 5 个快捷表情中挑选</div></div>
            <div class="setting-content">
              <select class="select" id="autoEmoteMode">
                <option value="quickbar" ${(s.autoEmoteMode || 'quickbar') === 'quickbar' ? 'selected' : ''}>快捷栏随机</option>
                <option value="specific" ${s.autoEmoteMode === 'specific' ? 'selected' : ''}>指定表情</option>
                <option value="library" ${s.autoEmoteMode === 'library' ? 'selected' : ''}>全部表情库随机</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">指定表情</div><div class="setting-sub">「表情来源」选择指定表情时生效</div></div>
            <div class="setting-content">
              <select class="select" id="autoEmoteId" style="max-width:260px">
                <option value="0">请选择表情</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">自动敲击</div><div class="setting-sub">自动产生敲击并上传；建议不超过 10 次/秒</div></div>
            <div class="switch ${s.autoClickEnabled ? 'on' : ''}" data-switch="autoClickEnabled"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">自动敲击频率</div><div class="setting-sub">每秒产生敲击次数，范围 1–10</div></div>
            <div class="setting-content"><input class="input" id="autoClickRate" type="number" min="1" max="10" step="1" value="${s.autoClickPerSecond ?? 10}" style="width:86px" /><span class="muted">次/秒</span></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">显示虚拟敲击动作</div><div class="setting-sub">关闭后只响应真实敲击；键位显示器始终不响应虚拟敲击</div></div>
            <div class="switch ${s.autoClickAnimate !== false ? 'on' : ''}" data-switch="autoClickAnimate"></div>
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
            <div><div class="setting-title">开机自动启动</div><div class="setting-sub">登录 Windows 后自动运行桌宠（仅当前用户；管理员模式使用最高权限计划任务）</div></div>
            <div class="switch ${s.launchAtLogin ? 'on' : ''}" data-switch="launchAtLogin"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">以管理员权限启动</div><div class="setting-sub">${s.isElevated ? '当前已是管理员权限' : '当前为普通权限'}；用于统计以管理员权限运行的游戏/程序中的按键。开启会请求 UAC 并重启；开机自启由最高权限计划任务完成，登录时不再提示</div></div>
            <div class="switch ${s.runAsAdmin ? 'on' : ''}" data-switch="runAsAdmin"></div>
          </div>
          <div class="setting-item" style="align-items:flex-start">
            <div><div class="setting-title">游戏模式规则</div><div class="setting-sub">检测到以下进程启动时自动开启鼠标穿透，进程退出后恢复</div></div>
            <div class="setting-content" style="flex-direction:column;align-items:stretch;gap:8px;min-width:280px">
              <div style="display:flex;gap:8px">
                <input class="input" id="gameRuleInput" placeholder="进程名，如 WutheringWaves.exe" style="flex:1;min-width:180px" />
                <button class="btn ghost" id="gameRuleAdd">添加</button>
              </div>
              <div id="gameRuleList" class="rule-list">
                ${(s.gameModeRules || []).length
                  ? (s.gameModeRules || []).map(name => `<span class="rule-chip">${esc(name)}<button type="button" data-remove-rule="${esc(name)}" title="删除规则">×</button></span>`).join('')
                  : '<span class="muted" style="font-size:12px">暂无规则</span>'}
              </div>
              <div class="setting-sub" style="margin-top:0">可填写任务管理器中的进程名，带不带 .exe 均可；匹配不区分大小写。</div>
            </div>
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
        ${compatibilityBannerHtml()}
        <div class="settings-footer">
          <button class="btn ghost" id="checkUpdateBtn">检查更新</button>
          <button class="btn ghost" id="compatRefreshBtn">刷新版本状态</button>
          <span class="muted" id="updateCheckStatus"></span>
        </div>
      </div>`);

    document.getElementById('updateDismiss')?.addEventListener('click', () => {
      state.updateDismissed = true;
      renderSettings();
    });
    bindSettings();
    loadAutoEmoteOptions().catch(() => { });
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
    { id: 'capture', label: 'OBS 绿幕窗口', def: '' },
    { id: 'mainPanel', label: '打开主面板', def: '' },
    { id: 'catLayout', label: '切换横纵排列', def: '' },
    { id: 'keyBubbles', label: '开关按键气泡', def: '' },
    { id: 'rhythmMode', label: '开关音游模式', def: '' },
    { id: 'autoEmote', label: '开关自动表情', def: '' },
    { id: 'autoClick', label: '开关自动敲击', def: '' },
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
    bindChestButtons();
    const scaleSel = document.getElementById('setScale');
    const scaleRange = document.getElementById('setScaleRange');
    const scaleVal = document.getElementById('scaleVal');
    const scalePresets = [25, 50, 75, 100, 125, 150, 200];
    let lastScaleSent = 0;
    const syncScaleSelect = (pct) => {
      if (!scaleSel) return;
      scaleSel.value = scalePresets.includes(pct) ? String(pct / 100) : 'custom';
    };
    const applyScale = async (pct, final = false) => {
      pct = Math.max(25, Math.min(200, Math.round(Number(pct) || 100)));
      if (scaleVal) scaleVal.textContent = pct + '%';
      if (scaleRange) scaleRange.value = String(pct);
      syncScaleSelect(pct);
      if (state.boot?.settings) state.boot.settings.scale = pct / 100;
      if (document.getElementById('remoteFollowMain')?.checked) {
        const rr = document.getElementById('setRemoteScaleRange');
        const rv = document.getElementById('remoteScaleVal');
        const rs = document.getElementById('setRemoteScale');
        if (rr) rr.value = String(pct);
        if (rv) rv.textContent = pct + '%';
        if (rs) rs.value = scalePresets.includes(pct) ? String(pct / 100) : 'custom';
      }
      try {
        const boot = await tryCall('settings.set', { scale: pct / 100 });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    scaleSel?.addEventListener('change', e => {
      if (e.target.value === 'custom') return;
      lastScaleSent = performance.now();
      applyScale(Number(e.target.value) * 100, true);
    });
    scaleRange?.addEventListener('input', () => {
      const pct = Number(scaleRange.value);
      if (scaleVal) scaleVal.textContent = pct + '%';
      syncScaleSelect(pct);
      // 拖动时约每 80ms 应用一次，既能实时缩放，也不会每移动 1% 就写一次配置。
      const now = performance.now();
      if (now - lastScaleSent >= 80) {
        lastScaleSent = now;
        applyScale(pct, false);
      }
    });
    scaleRange?.addEventListener('change', () => {
      lastScaleSent = performance.now();
      applyScale(Number(scaleRange.value), true);
    });
    const remoteFollow = document.getElementById('remoteFollowMain');
    const remoteScaleSel = document.getElementById('setRemoteScale');
    const remoteScaleRange = document.getElementById('setRemoteScaleRange');
    const remoteScaleVal = document.getElementById('remoteScaleVal');
    const remoteScalePresets = [25, 50, 75, 100, 125, 150, 200];
    let lastRemoteScaleSent = 0;
    const syncRemoteScaleSelect = (pct) => {
      if (!remoteScaleSel) return;
      remoteScaleSel.value = remoteScalePresets.includes(pct) ? String(pct / 100) : 'custom';
    };
    const setRemoteControlsEnabled = (follow) => {
      if (remoteScaleSel) remoteScaleSel.disabled = follow;
      if (remoteScaleRange) remoteScaleRange.disabled = follow;
    };
    const applyRemoteScale = async (pct, final = false) => {
      pct = Math.max(25, Math.min(200, Math.round(Number(pct) || 100)));
      if (remoteScaleVal) remoteScaleVal.textContent = pct + '%';
      if (remoteScaleRange) remoteScaleRange.value = String(pct);
      syncRemoteScaleSelect(pct);
      if (state.boot?.settings) {
        state.boot.settings.remoteScale = pct / 100;
        state.boot.settings.remoteFollowMain = false;
      }
      try {
        const boot = await tryCall('settings.set', { remoteScale: pct / 100, remoteFollowMain: false });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    remoteFollow?.addEventListener('change', async () => {
      const follow = !!remoteFollow.checked;
      setRemoteControlsEnabled(follow);
      if (follow) {
        try {
          const boot = await tryCall('settings.set', { remoteFollowMain: true });
          if (boot?.settings) state.boot = boot;
          renderSettings();
        } catch { }
        return;
      }
      const mainPct = Math.max(25, Math.min(200, Math.round((Number(state.boot?.settings?.scale) || 1) * 100)));
      // 从“跟随主角”切到自定义时，以当前主角大小作为起点，避免滑条突然跳到旧值。
      if (remoteScaleRange) remoteScaleRange.value = String(mainPct);
      if (remoteScaleVal) remoteScaleVal.textContent = mainPct + '%';
      syncRemoteScaleSelect(mainPct);
      try {
        const boot = await tryCall('settings.set', { remoteFollowMain: false, remoteScale: mainPct / 100 });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    });
    remoteScaleSel?.addEventListener('change', e => {
      if (e.target.value === 'custom') return;
      lastRemoteScaleSent = performance.now();
      applyRemoteScale(Number(e.target.value) * 100, true);
    });
    remoteScaleRange?.addEventListener('input', () => {
      const pct = Number(remoteScaleRange.value);
      if (remoteScaleVal) remoteScaleVal.textContent = pct + '%';
      syncRemoteScaleSelect(pct);
      const now = performance.now();
      if (now - lastRemoteScaleSent >= 80) {
        lastRemoteScaleSent = now;
        applyRemoteScale(pct, false);
      }
    });
    remoteScaleRange?.addEventListener('change', () => {
      lastRemoteScaleSent = performance.now();
      applyRemoteScale(Number(remoteScaleRange.value), true);
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
    const remoteFollowOpacity = document.getElementById('remoteFollowMainOpacity');
    const remoteOpacityRange = document.getElementById('setRemoteOpacityRange');
    const remoteOpacityVal = document.getElementById('remoteOpacityVal');
    let lastRemoteOpacitySent = 0;
    const syncRemoteOpacityUi = (pct) => {
      if (remoteOpacityRange) remoteOpacityRange.value = String(pct);
      if (remoteOpacityVal) remoteOpacityVal.textContent = pct + '%';
    };
    const applyRemoteOpacity = async (pct, final = false) => {
      pct = Math.max(20, Math.min(100, Math.round(Number(pct) || 100)));
      syncRemoteOpacityUi(pct);
      if (state.boot?.settings) {
        state.boot.settings.remoteOpacity = pct / 100;
        state.boot.settings.remoteFollowMainOpacity = false;
      }
      try {
        const boot = await tryCall('settings.set', { remoteOpacity: pct / 100, remoteFollowMainOpacity: false });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    remoteFollowOpacity?.addEventListener('change', async () => {
      const follow = !!remoteFollowOpacity.checked;
      if (remoteOpacityRange) remoteOpacityRange.disabled = follow;
      if (follow) {
        try {
          const boot = await tryCall('settings.set', { remoteFollowMainOpacity: true });
          if (boot?.settings) state.boot = boot;
          renderSettings();
        } catch { }
        return;
      }
      const mainPct = Math.max(20, Math.min(100, Math.round((Number(state.boot?.settings?.opacity) || 1) * 100)));
      syncRemoteOpacityUi(mainPct);
      try {
        const boot = await tryCall('settings.set', { remoteFollowMainOpacity: false, remoteOpacity: mainPct / 100 });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    });
    remoteOpacityRange?.addEventListener('input', () => {
      const pct = Math.max(20, Math.min(100, Math.round(Number(remoteOpacityRange.value) || 100)));
      if (remoteOpacityVal) remoteOpacityVal.textContent = pct + '%';
      const now = performance.now();
      if (now - lastRemoteOpacitySent >= 80) {
        lastRemoteOpacitySent = now;
        applyRemoteOpacity(pct, false);
      }
    });
    remoteOpacityRange?.addEventListener('change', () => {
      lastRemoteOpacitySent = performance.now();
      applyRemoteOpacity(Number(remoteOpacityRange.value), true);
    });
    const remoteNameScale = document.getElementById('setRemoteNameScale');
    const remoteNameScaleVal = document.getElementById('remoteNameScaleVal');
    remoteNameScale?.addEventListener('input', () => {
      if (remoteNameScaleVal) remoteNameScaleVal.textContent = remoteNameScale.value + '%';
    });
    remoteNameScale?.addEventListener('change', async () => {
      const pct = Math.max(50, Math.min(200, Math.round(Number(remoteNameScale.value) || 100)));
      if (remoteNameScaleVal) remoteNameScaleVal.textContent = pct + '%';
      try {
        const boot = await tryCall('settings.set', { remoteNameScale: pct / 100 });
        if (boot?.settings) state.boot = boot;
      } catch { }
    });
    const op = document.getElementById('setOpacity');
    op?.addEventListener('input', () => {
      const pct = Math.max(20, Math.min(100, Math.round(Number(op.value) || 100)));
      const label = document.getElementById('opacityVal');
      if (label) label.textContent = pct + '%';
      if (remoteFollowOpacity?.checked) syncRemoteOpacityUi(pct);
    });
    op?.addEventListener('change', async () => {
      try { await tryCall('settings.set', { opacity: Number(op.value) / 100 }); await syncBoot(); } catch { }
    });
    const hp = document.getElementById('setHoverOpacity');
    hp?.addEventListener('input', () => { document.getElementById('hoverOpacityVal').textContent = hp.value + '%'; });
    hp?.addEventListener('change', async () => {
      try { await tryCall('settings.set', { hoverFadeOpacity: Number(hp.value) / 100 }); await syncBoot(); } catch { }
    });
    const gameRuleInput = document.getElementById('gameRuleInput');
    const saveGameRules = async (rules) => {
      try {
        const boot = await tryCall('settings.set', { gameModeRules: rules });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    };
    const addGameRule = () => {
      const name = String(gameRuleInput?.value || '').trim();
      if (!name) return;
      const rules = [...(state.boot?.settings?.gameModeRules || []), name];
      if (gameRuleInput) gameRuleInput.value = '';
      saveGameRules(rules);
    };
    document.getElementById('gameRuleAdd')?.addEventListener('click', addGameRule);
    gameRuleInput?.addEventListener('keydown', e => {
      if (e.key === 'Enter') { e.preventDefault(); addGameRule(); }
    });
    document.querySelectorAll('[data-remove-rule]').forEach(el => {
      el.addEventListener('click', () => {
        const name = el.dataset.removeRule;
        saveGameRules((state.boot?.settings?.gameModeRules || []).filter(x => x !== name));
      });
    });

    document.querySelectorAll('[data-switch]').forEach(el => el.onclick = async () => {
      const key = el.dataset.switch;
      const next = !el.classList.contains('on');
      el.classList.toggle('on', next);
      if (key === 'runAsAdmin' && next) toast('正在请求管理员权限…');
      try {
        await tryCall('settings.set', { [key]: next });
        await syncBoot();
        if (key === 'rhythmMode' || key === 'autoEmoteEnabled' || key === 'autoClickEnabled') { renderSettings(); return; }
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
    const bindAutoRate = (id, max, key, fallback) => {
      const el = document.getElementById(id);
      if (!el) return;
      let timer = 0;
      const save = async () => {
        clearTimeout(timer);
        const value = Math.max(1, Math.min(max, Math.round(Number(el.value) || fallback)));
        el.value = value;
        try { await tryCall('settings.set', { [key]: value }); await syncBoot(); } catch { }
      };
      el.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(save, 400); });
      el.addEventListener('change', save);
      el.addEventListener('blur', save);
    };
    bindAutoRate('autoEmoteRate', 20, 'autoEmoteMaxPerSecond', 20);
    document.getElementById('autoEmoteMode')?.addEventListener('change', async e => {
      try { await tryCall('settings.set', { autoEmoteMode: e.target.value }); await syncBoot(); renderSettings(); } catch { }
    });
    document.getElementById('autoEmoteId')?.addEventListener('change', async e => {
      try { await tryCall('settings.set', { autoEmoteId: Number(e.target.value) || 0 }); await syncBoot(); } catch { }
    });
    bindAutoRate('autoClickRate', 10, 'autoClickPerSecond', 10);
    document.querySelectorAll('[data-nudge]').forEach(el => el.onclick = async () => {
      const [dx, dy] = String(el.dataset.nudge || '0,0').split(',').map(Number);
      try {
        const r = await tryCall('appearance.offset', { dx, dy });
        if (r?.boot) state.boot = r.boot;
        renderChrome();
        renderSettings();
      } catch { }
    });
    document.getElementById('resetAccessoryOffset')?.addEventListener('click', async () => {
      try {
        const r = await tryCall('appearance.offset', { reset: true });
        if (r?.boot) state.boot = r.boot;
        renderChrome();
        renderSettings();
      } catch { }
    });
    const rhythmInputs = [...document.querySelectorAll('[data-rhythm-key]')];
    const rhythmNormalize = (value) => {
      let v = String(value || '').trim();
      if (v === ' ' || v === '␣' || /^space$/i.test(v)) return '␣';
      if (v.length > 1) v = v.slice(-1);
      if (/^[A-Za-z0-9]$/.test(v)) return v.toUpperCase();
      if (";',./[]=-`\\".includes(v)) return v;
      return '';
    };
    const rhythmToken = (value) => rhythmNormalize(value) === '␣' ? 'SPACE' : rhythmNormalize(value);
    const rhythmCodeMap = {
      Space: '␣', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/',
      BracketLeft: '[', BracketRight: ']', Minus: '-', Equal: '=', Backquote: '`', Backslash: '\\'
    };
    const rhythmFromEvent = (e) => {
      if (rhythmCodeMap[e.code]) return rhythmCodeMap[e.code];
      if (/^Key[A-Z]$/.test(e.code)) return e.code.slice(3);
      if (/^Digit[0-9]$/.test(e.code)) return e.code.slice(5);
      return null;
    };
    rhythmInputs.forEach(input => {
      input.addEventListener('focus', () => input.select());
      input.addEventListener('keydown', e => {
        if (e.ctrlKey || e.altKey || e.metaKey) return;
        const value = rhythmFromEvent(e);
        if (value === null) return;
        // 直接按键替换当前键位，不需要先删除原值。
        e.preventDefault();
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
      });
      input.addEventListener('input', () => { input.value = rhythmNormalize(input.value); });
      input.addEventListener('change', async () => {
        const keys = rhythmInputs.map(x => rhythmToken(x.value));
        if (keys.some(k => !(k === 'SPACE' || /^[A-Z0-9]$/.test(k) || ";',./[]=-`\\".includes(k)))) return toast('音游键位包含不支持的字符', 'err');
        if (new Set(keys).size !== keys.length) return toast('触发键位不能重复', 'err');
        try {
          await tryCall('settings.set', { rhythmKeys: keys });
          await syncBoot();
          renderSettings();
          toast('音游键位已保存');
        } catch { }
      });
    });
    document.getElementById('setRhythmKeyCount')?.addEventListener('change', async e => {
      try {
        await tryCall('settings.set', { rhythmKeyCount: Number(e.target.value) });
        await syncBoot();
        renderSettings();
      } catch { }
    });
    document.getElementById('openLog')?.addEventListener('click', () => call('open.log'));
    document.getElementById('aboutBtn')?.addEventListener('click', showAbout);
    document.getElementById('checkUpdateBtn')?.addEventListener('click', async () => {
      const btn = document.getElementById('checkUpdateBtn');
      btn.disabled = true;
      btn.textContent = '检查中…';
      try {
        const r = await tryCall('update.check');
        state.update = r?.update || null;
        state.updateDismissed = false;
        renderSettings();
        if (state.update) {
          toast('发现新版本 ' + (state.update.tag || ''));
          requestAnimationFrame(() => { const scroller = document.querySelector('.settings-scroll'); if (scroller) scroller.scrollTop = 0; });
        } else {
          toast('当前已经是最新版本');
        }
      } catch {
        btn.disabled = false;
        btn.textContent = '检查更新';
      }
    });
    document.getElementById('compatRefreshBtn')?.addEventListener('click', async () => {
      const btn = document.getElementById('compatRefreshBtn');
      btn.disabled = true;
      btn.textContent = '刷新中…';
      try {
        const r = await tryCall('compatibility.check');
        if (r) {
          state.compatibility = r;
          renderNotices();
          renderSettings();
          toast('版本状态已更新');
        } else {
          toast('暂时无法获取版本状态', 'err');
        }
      } catch {
        toast('版本状态刷新失败', 'err');
      } finally {
        btn.disabled = false;
        btn.textContent = '刷新版本状态';
      }
    });
    bindHotkeys();
    document.getElementById('redeemBtn')?.addEventListener('click', async () => {
      const input = document.getElementById('redeemCode');
      const code = input.value.trim();
      if (!code) return toast('请输入兑换码', 'err');
      try {
        await redeemCode(code);
        input.value = '';
      } catch { }
    });
    document.getElementById('logoutBtn')?.addEventListener('click', async () => {
      try {
        await tryCall('logout');
        // 退出后必须重新加载 WebView，清空背包/图鉴/预设等上一个账号的页面缓存。
        window.location.reload();
      } catch { }
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
    document.getElementById('setServerRegion')?.addEventListener('change', async e => {
      const val = e.target.value;
      if (val !== (state.boot?.settings?.serverRegion || 'cn')) {
        const boot = await tryCall('settings.set', { serverRegion: val });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      }
    });
    document.querySelectorAll('#setAnimationFps [data-fps]').forEach(btn => {
      btn.onclick = async () => {
        const fps = Number(btn.dataset.fps);
        try {
          await tryCall('settings.set', { animationFps: fps });
          if (state.boot?.settings) state.boot.settings.animationFps = fps;
          renderSettings();
          toast(`动画帧率已设为 ${fps} 帧`);
        } catch { }
      };
    });
    document.querySelectorAll('#setCatLayout [data-layout]').forEach(btn => {
      btn.onclick = async () => {
        const val = btn.dataset.layout === 'vertical' ? 'vertical' : 'horizontal';
        try {
          await tryCall('settings.set', { catLayout: val });
          if (state.boot?.settings) state.boot.settings.catLayout = val;
          await tryCall('room.resetOrder');
          renderSettings();
          toast(val === 'vertical' ? '已切换为纵向排列' : '已切换为横向排列');
        } catch { }
      };
    });
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
    document.getElementById('setPhoneLoginOpen')?.addEventListener('click', () => {
      state.loginMode = 'phone';
      navigate('bag');
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

  // 仅供本地浏览器预览更新提示：?preview=verified|unverified|forced|minSafe|unavailable
  function previewBoot(mode) {
    return {
      version: '0.1.4',
      disclaimerShown: true,
      uacPromptShown: true,
      auth: {
        loggedIn: true,
        nickname: 'Daniel兔兔',
        uid: '103242655',
        clicks: 114514,
        clickTotal: 120000,
        clickConsume: 5486,
      },
      room: mode === 'room'
        ? {
            joined: true,
            roomNo: 'RYOlbDDRN',
            lastRoom: '',
            history: [],
            hiddenAll: false,
            members: [
              { id: 1, nickname: 'Daniel兔兔', owner: true, self: true, online: true, visible: true },
              { id: 2, nickname: '在线成员', owner: false, self: false, online: true, visible: true },
              { id: 3, nickname: '在线房主', owner: true, self: false, online: true, visible: false },
              { id: 4, nickname: '离线成员', owner: false, self: false, online: false, visible: true },
            ],
          }
        : { joined: false, members: [], history: [], roomNo: '', lastRoom: '' },
      settings: {
        scale: 0.83,
        opacity: 1,
        remoteScale: 1,
        remoteFollowMain: true,
        remoteOpacity: 1,
        remoteFollowMainOpacity: true,
        remoteNameScale: 1.19,
        topmost: true,
        clickThrough: false,
        hoverFade: true,
        keyboardMode: 'hidden',
        showMappingControls: true,
        hideOtherPets: false,
        allowOverlap: true,
        catLayout: 'vertical',
        captureMode: false,
        autoChest: true,
        autoEmoteEnabled: false,
        autoClickEnabled: false,
        notifyChestClaimed: false,
        notifyHotkeys: false,
        launchAtLogin: true,
        runAsAdmin: false,
        serverRegion: 'cn',
        gameModeRules: [],
        hotkeys: {},
      },
      appearance: {
        characterAssetId: 3060,
        accessoryAssetId: 1604,
        emoteAssetIds: [1971, 1965, 1969, 1970, 1968],
      },
      catalog: {},
      update: null,
    };
  }

  function previewCompatibility(mode) {
    const checkedAt = new Date().toISOString();
    const base = {
      schema: 1,
      updatedAt: checkedAt,
      status: 'safe',
      message: 'Wuwatappo Native 0.1.4 已发布；最低兼容版本为 0.1.3。官方客户端 1.0.4 为非强制更新，当前版本可继续使用。',
      native: { latest: '0.1.4', minSafe: '0.1.3' },
      official: {
        cn: { latest: '1.0.4', minOta: '1.0.0' },
        global: { latest: '1.0.4', minOta: '1.0.0' },
      },
      protocol: { testedOfficial: '1.0.4', generation: 1, breaking: false },
      killSwitch: { forceOffline: false, reason: '' },
    };
    const official = (state, version, forced, message) => ({
      state,
      officialVersion: version,
      testedVersion: '1.0.4',
      forceManualDownload: forced,
      minOtaClientVersion: '1.0.0',
      message,
      checkedAt,
      stale: false,
    });
    if (mode === 'unverified') {
      base.status = 'warning';
      base.message = '官方客户端已更新至 1.0.5，当前 Native 尚未验证该版本。';
      base.official.cn.latest = '1.0.5';
      base.official.global.latest = '1.0.5';
      base.officialCheck = official('unverified', '1.0.5', false,
        '官方客户端已更新至 1.0.5，当前 Native 尚未验证该版本。大多数情况下，如果当前功能正常，不必立即更新；如果联机异常，建议暂停联机并等待 Native 更新。');
    } else if (mode === 'forced') {
      base.status = 'unsafe';
      base.message = '官方客户端已强制更新至 1.0.5，旧版 Native 的联机功能可能已经失效。';
      base.official.cn.latest = '1.0.5';
      base.official.global.latest = '1.0.5';
      base.officialCheck = official('forced', '1.0.5', true,
        '官方客户端已强制更新至 1.0.5，旧版 Native 的联机功能可能已经失效。建议暂停联机并等待 Native 更新；单机功能仍可继续使用。');
    } else if (mode === 'minSafe') {
      base.status = 'warning';
      base.message = '当前 Native 0.1.4 低于最低兼容版本 0.1.5，不保证兼容当前官方服务。';
      base.native.minSafe = '0.1.5';
      base.officialCheck = official('verified', '1.0.4', false, '当前 Native 已验证官方 1.0.4，可继续使用。');
    } else if (mode === 'unavailable') {
      base.officialCheck = official('unavailable', '', false, '暂时无法检查官方版本，不影响当前使用。');
    } else {
      base.officialCheck = official('verified', '1.0.4', false, '当前 Native 已验证官方 1.0.4，可继续使用。');
    }
    return base;
  }

  function previewAnnouncements(mode) {
    const base = {
      publishedAt: '2026-11-07T00:00:00.000Z',
      dismissible: true,
      action: { label: '查看详情', url: 'https://github.com/DanielToyama/WuwaTappoNative' },
    };
    if (mode === 'announcement-warning') {
      return [{
        ...base,
        id: 'preview-warning',
        title: '今晚 23:00 服务维护',
        body: '维护期间联机功能可能短暂中断，单机与桌宠功能不受影响。\n预计持续 1 小时。',
        severity: 'warning',
      }];
    }
    if (mode === 'announcement-critical') {
      return [{
        ...base,
        id: 'preview-critical',
        title: '请更新后继续使用联机',
        body: '当前版本与官方服务存在兼容风险；单机桌宠仍可正常使用。',
        severity: 'critical',
        dismissible: false,
        action: { label: '前往下载', url: 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest' },
      }];
    }
    return [{
      ...base,
      id: 'preview-info',
      title: '0.1.7 版本说明',
      body: '本次更新修复了手机号登录和动态 SDK 配置问题。\n公告只在设置页顶部显示，不打扰其他功能。',
      severity: 'info',
    }, {
      ...base,
      id: 'preview-info-2',
      title: '第二条公告',
      body: '关闭上一条后才会显示这一条。',
      severity: 'info',
      action: null,
    }];
  }

  if (PREVIEW) {
    state.boot = previewBoot(PREVIEW);
    state.compatibility = previewCompatibility(PREVIEW);
    state.announcements = previewAnnouncements(PREVIEW);
    if (PREVIEW === 'synth') {
      state.bag = [
        { id: 101, type: 'character_skin', name: '测试角色材料', rarity: 'rare', quantity: 11, thumb: '' },
        { id: 102, type: 'accessory', name: '测试配件材料', rarity: 'uncommon', quantity: 7, thumb: '' },
        { id: 103, type: 'emote', name: '测试表情材料', rarity: 'common', quantity: 3, thumb: '' },
      ];
    }
    state.page = PREVIEW === 'room' ? 'room' : (PREVIEW === 'synth' ? 'synth' : 'settings');
    init();
    renderChrome();
    renderPage();
  } else {
    init();
    boot();
  }
})();
