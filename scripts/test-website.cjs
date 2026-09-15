const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../website/index.html'), 'utf8');
const script = fs.readFileSync(path.join(__dirname, '../website/app.js'), 'utf8');

function builder() {
  const elements = new Map();
  for (const match of html.matchAll(/<([\w-]+)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    const attrs = match[2];
    const element = {
      value: /\bvalue="([^"]*)"/.exec(attrs)?.[1] ?? '',
      disabled: /\bdisabled\b/.test(attrs), textContent: '', checked: false,
      pattern: /\bpattern="([^"]*)"/.exec(attrs)?.[1], required: /\brequired\b/.test(attrs),
      setAttribute() {}, addEventListener() {},
    };
    assert(!elements.has(`#${match[3]}`), `Duplicate HTML id ${match[3]}`);
    elements.set(`#${match[3]}`, element);
  }
  elements.get('#compression').value = 'Lz4';
  elements.get('#encryption').value = 'None';
  elements.get('#command-form').checkValidity = () => [...elements.values()].every(el => el.disabled ||
    ((!el.required || !!el.value) && (!el.pattern || new RegExp(`^(?:${el.pattern})$`, 'u').test(el.value))));
  const context = vm.createContext({ document: {
    querySelector: id => { assert(elements.has(id), `Missing HTML element ${id}`); return elements.get(id); },
    querySelectorAll: () => [],
  } });
  vm.runInContext(script.slice(0, script.indexOf('function flattenedCommand')), context);
  return { elements, context, run: code => vm.runInContext(code, context) };
}

test('encryption command matrix: both roles, every cipher, every shell', () => {
  const b = builder();
  for (const role of ['client', 'server']) for (const shell of ['bash', 'powershell', 'cmd'])
    for (const cipher of ['None', 'ChaCha20Poly1305', 'AesGcm', 'AesCcm']) {
      b.elements.get('#encryption').value = cipher;
      b.run(`applyRole('${role}'); tunnelState.shell = '${shell}'; updateTunnelCommand();`);
      const output = b.elements.get('#command').textContent;
      assert.equal(b.elements.get('#copy-command').disabled, false);
      assert(output.includes(`--mode ${role === 'client' ? 'Client' : 'Server'}`));
      if (cipher === 'None') assert(!output.includes('--encryption:'));
      else {
        assert(output.includes(`--encryption:algorithm ${cipher}`));
        assert(output.includes(role === 'client' ? '--encryption:key-id laptop --encryption:key REPLACE_WITH_GENERATED_KEY' : '--encryption:keys:laptop REPLACE_WITH_GENERATED_KEY'));
        assert(b.elements.get('#command-note').textContent.includes('--generate-key'));
      }
      assert(output.includes(shell === 'bash' ? '\\\n' : shell === 'powershell' ? '`\n' : '^\n'));
    }
});

test('invalid key IDs cannot be copied into a shell command', () => {
  const b = builder(); b.elements.get('#encryption').value = 'AesGcm';
  for (const id of ['', 'client;whoami', 'client$(whoami)', 'client%PATH%', 'client`whoami', 'client name']) {
    b.elements.get('#key-id').value = id; b.run('updateTunnelCommand()');
    assert.equal(b.elements.get('#copy-command').disabled, true);
    assert(!b.elements.get('#command').textContent.includes('--encryption:key'));
  }
  b.elements.get('#encryption').value = 'None'; b.run('updateTunnelCommand()');
  assert.equal(b.elements.get('#copy-command').disabled, false);
});
