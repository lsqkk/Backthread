const windows = [
  { title: '年度方案 · 文档', app: '文档编辑器', detail: '停留 23 分钟', label: '建议返回', icon: '▤' },
  { title: '项目对话 · 聊天', app: '聊天', detail: '停留 49 秒', icon: '◇' },
  { title: '数据校验 · 表格', app: '表格编辑器', detail: '停留 4 分钟', icon: '▦' },
  { title: '资料检索 · 浏览器', app: '浏览器', detail: '当前窗口', icon: '◫' },
];
const checkpoints = [{ ...windows[0], title: '从第 3 段继续改', detail: windows[0].title, label: '栈顶', icon: '◇', target: windows[0] }];
const query = document.querySelector('#query');
const results = document.querySelector('#results');
const feedback = document.querySelector('#feedback');
const openButton = document.querySelector('#select');
const recent = document.querySelector('#recent');
const stack = document.querySelector('#stack');
const mockTitle = document.querySelector('#mock-title');
let current = windows[3];
let view = 'recent';
let selected = 0;
let visible = [];

function render() {
  const search = query.value.trim().toLowerCase();
  const source = view === 'recent' ? windows.filter(window => window !== current) : checkpoints;
  visible = source.filter(window => `${window.title} ${window.app} ${window.detail}`.toLowerCase().includes(search));
  selected = Math.min(selected, Math.max(visible.length - 1, 0));
  results.replaceChildren();
  visible.forEach((window, index) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'result';
    button.setAttribute('aria-current', String(index === selected));
    const symbol = document.createElement('span');
    symbol.className = 'result-symbol';
    symbol.setAttribute('aria-hidden', 'true');
    symbol.textContent = window.icon;
    const text = document.createElement('span');
    const title = document.createElement('strong');
    title.textContent = window.title;
    const detail = document.createElement('small');
    detail.textContent = `${window.app} · ${window.detail === '当前窗口' ? '最近使用' : window.detail}`;
    text.append(title, detail);
    const label = document.createElement('span');
    label.className = 'result-label';
    label.textContent = window.label ?? '';
    button.append(symbol, text, label);
    button.addEventListener('click', () => open(window));
    results.append(button);
  });
  if (!visible.length) {
    const empty = document.createElement('p');
    empty.className = 'empty';
    empty.textContent = search ? '没有匹配的窗口。' : '复工栈已空。';
    results.append(empty);
  }
  openButton.disabled = visible.length === 0;
}

function open(window) {
  if (!window) return;
  current = window.target ?? window;
  mockTitle.textContent = current.title;
  if (view === 'stack') checkpoints.pop();
  feedback.textContent = `已返回 ${current.title}`;
  selected = 0;
  render();
  query.focus();
}

function changeView(next) {
  view = next;
  selected = 0;
  query.value = '';
  recent.setAttribute('aria-pressed', String(view === 'recent'));
  stack.setAttribute('aria-pressed', String(view === 'stack'));
  feedback.textContent = '↑ ↓ 选择 · ↵ 返回';
  render();
  query.focus();
}

recent.addEventListener('click', () => changeView('recent'));
stack.addEventListener('click', () => changeView('stack'));
openButton.addEventListener('click', () => open(visible[selected]));
query.addEventListener('input', () => { selected = 0; render(); });
query.addEventListener('keydown', event => {
  if (event.key === 'Enter') { event.preventDefault(); open(visible[selected]); }
  if (!visible.length || !['ArrowUp', 'ArrowDown'].includes(event.key)) return;
  event.preventDefault();
  selected = Math.max(0, Math.min(visible.length - 1, selected + (event.key === 'ArrowUp' ? -1 : 1)));
  render();
});
render();
