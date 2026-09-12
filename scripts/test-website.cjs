const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../website/index.html'), 'utf8');
const script = fs.readFileSync(path.join(__dirname, '../website/app.js'), 'utf8');

test('website describes dual-stack ACLs, service operation, logging, and SQL Server origin', () => {
  for (const text of ['IPv4', 'IPv6', 'CIDR', 'Windows services', 'systemd', 'connection attempts', 'Microsoft SQL Server (MSSQL)', 'compress and encrypt'])
    assert(html.includes(text), `Missing website capability: ${text}`);
});

function builder() {
  const elements = new Map();
  for (const match of html.matchAll(/<([\w-]+)\b([^>]*\bid="([^"]+)"[^>]*)>/g)) {
    const attrs = match[2];
    const element = {
      value: /\bvalue="([^"]*)"/.exec(attrs)?.[1] ?? '',
      disabled: /\bdisabled\b/.test(attrs), textContent: '', checked: false,
      pattern: /\bpattern="([^"]*)"/.exec(attrs)?.[1], required: /\brequired\b/.test(attrs),
      setAttribute() {}, addEventListener() {}, closest() { return {}; },
    };
    assert(!elements.has(`#${match[3]}`), `Duplicate HTML id ${match[3]}`);
    elements.set(`#${match[3]}`, element);
  }
  elements.get('#compression').value = 'Lz4';
  elements.get('#encryption').value = 'None';
  elements.get('#tls-mode').value = 'None';
  elements.get('#tls-protocols').value = 'Tls12, Tls13';
  elements.get('#tls-cipher').value = '';
  elements.get('#tls-certificate').value = 'generated';
  elements.get('#command-form').checkValidity = () => [...elements.values()].every(el => el.disabled ||
    ((!el.required || !!el.value) && (!el.value || !el.pattern || new RegExp(`^(?:${el.pattern})$`, 'u').test(el.value))));
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

test('TLS command matrix selects the correct endpoint and certificate controls', () => {
  const b = builder();
  for (const role of ['client', 'server']) for (const mode of ['None', 'Tls', 'SqlServer', 'SqlServerStrict'])
    for (const shell of ['bash', 'powershell', 'cmd']) {
      b.elements.get('#tls-mode').value = mode;
      b.elements.get('#tls-certificate').value = 'generated';
      b.elements.get('#tls-trust').checked = true;
      b.run('applyRole(' + JSON.stringify(role) + '); tunnelState.shell = ' + JSON.stringify(shell) + '; updateTunnelCommand();');
      assert.equal(b.elements.get('#copy-command').disabled, false);
      const command = b.elements.get('#command').textContent;
      if (mode === 'None') {
        assert(!command.includes('--listen-tls:'));
        assert(!command.includes('--remote-tls:'));
      } else {
        assert(command.includes('--' + (role === 'client' ? 'listen-tls' : 'remote-tls') + ':mode ' + mode));
        if (role === 'client') {
          assert(!command.includes('--remote-tls:'));
          assert(command.includes(mode === 'SqlServerStrict' ? '--listen-tls:certificate-path "client.pfx"' : '--listen-tls:generate-self-signed'));
        } else {
          assert(!command.includes('--listen-tls:'));
          assert.equal(command.includes('--remote-tls:trust-server-certificate'), mode !== 'SqlServerStrict');
        }
        if (mode === 'SqlServer') assert(command.includes(':protocols "Tls12"'));
        if (mode === 'SqlServerStrict') assert.equal(b.elements.get('#tls-trust').checked, false);
      }
    }
});

test('TLS file paths are quoted and shell expansion is rejected', () => {
  const b = builder();
  b.elements.get('#tls-mode').value = 'Tls';
  b.elements.get('#tls-certificate').value = 'pem';
  b.elements.get('#tls-certificate-path').value = 'C:\\TLS files\\client.pem';
  b.elements.get('#tls-key-path').value = '/etc/tls/private/client.key';
  b.run('updateTunnelCommand()');
  assert.equal(b.elements.get('#copy-command').disabled, false);
  assert(b.elements.get('#command').textContent.includes('--listen-tls:certificate-path "C:\\TLS files\\client.pem"'));
  assert(b.elements.get('#command').textContent.includes('--listen-tls:certificate-key-path "/etc/tls/private/client.key"'));
  for (const path of ['bad";whoami', '$(whoami).pfx', '%PATH%.pfx', '!PATH!.pfx', String.fromCharCode(96) + 'whoami' + String.fromCharCode(96) + '.pfx', 'a&b.pfx', '']) {
    b.elements.get('#tls-certificate-path').value = path; b.run('updateTunnelCommand()');
    assert.equal(b.elements.get('#copy-command').disabled, true, path);
  }
});

test('TLS destination name and Linux cipher restriction appear in generated commands', () => {
  const b = builder();
  b.elements.get('#tls-mode').value = 'SqlServer';
  b.elements.get('#tls-target-host').value = 'sql.example';
  b.elements.get('#tls-cipher').value = 'TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384';
  b.run("applyRole('server')");
  const command = b.elements.get('#command').textContent;
  assert(command.includes('--remote-tls:target-host sql.example'));
  assert(command.includes('--remote-tls:cipher-suites TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384'));
  assert(b.elements.get('#command-note').textContent.includes('require Linux'));
});

test('form patterns are valid with browser Unicode set semantics', () => {
  for (const match of html.matchAll(/\bpattern="([^"]*)"/g)) new RegExp(match[1], 'v');
});
