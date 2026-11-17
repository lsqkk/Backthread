const windows = [...document.querySelectorAll('[data-window]')];
const feedback = document.querySelector('#feedback');
const mockTitle = document.querySelector('#mock-title');
let current = windows[0];
let previous = null;
let marked = null;

function activate(window) {
  if (!window || current === window) return;
  current.removeAttribute('aria-current');
  previous = current;
  current = window;
  current.setAttribute('aria-current', 'true');
  mockTitle.textContent = current.dataset.window;
}

for (const window of windows) {
  window.addEventListener('click', () => {
    activate(window);
    feedback.textContent = `正在使用「${current.dataset.window}」。`;
  });
}

document.querySelector('#mark-window').addEventListener('click', () => {
  marked = current;
  feedback.textContent = `已标记「${marked.dataset.window}」。试着打开另外两个窗口，再按返回。`;
});

document.querySelector('#return-window').addEventListener('click', () => {
  const target = marked && marked !== current ? marked : previous;
  if (!target) {
    feedback.textContent = '请先切换到另一个窗口。';
    return;
  }
  activate(target);
  if (current === marked) marked = null;
  feedback.textContent = `已返回「${current.dataset.window}」。`;
});
