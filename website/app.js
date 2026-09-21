const repository = 'tedd/Tedd.TcpTunnel';

const tunnelState = { role: 'client', shell: 'bash' };
const roleDefaults = {
  client: {
    name: 'app',
    listenAddress: '127.0.0.1',
    listenPort: '9000',
    remoteHost: 'tunnel.example',
    remotePort: '9001'
  },
  server: {
    name: 'server',
    listenAddress: '0.0.0.0',
    listenPort: '9001',
    remoteHost: '127.0.0.1',
    remotePort: '5432'
  }
};

const commandForm = document.querySelector('#command-form');
const commandOutput = document.querySelector('#command');
const commandCopy = document.querySelector('#copy-command');
const commandStatus = document.querySelector('#copy-status');
const compression = document.querySelector('#compression');
const compressionHistory = document.querySelector('#compression-history');
const encryption = document.querySelector('#encryption');
const keyId = document.querySelector('#key-id');
const tlsMode = document.querySelector('#tls-mode');
const tlsProtocols = document.querySelector('#tls-protocols');
const tlsCipher = document.querySelector('#tls-cipher');
const tlsCertificate = document.querySelector('#tls-certificate');
const tlsCertificatePath = document.querySelector('#tls-certificate-path');
const tlsKeyPath = document.querySelector('#tls-key-path');
const tlsName = document.querySelector('#tls-name');
const tlsTargetHost = document.querySelector('#tls-target-host');
const tlsTrust = document.querySelector('#tls-trust');


function selectButtons(selector, selected) {
  for (const button of document.querySelectorAll(selector)) {
    button.setAttribute('aria-pressed', String(button.dataset.role === selected || button.dataset.shell === selected || button.dataset.installShell === selected));
  }
}

function setChildren(container, children) {
  while (container.firstChild) container.removeChild(container.firstChild);
  for (const child of children) container.appendChild(child);
}

function applyRole(role) {
  tunnelState.role = role;
  const values = roleDefaults[role];
  document.querySelector('#forward-name').value = values.name;
  document.querySelector('#listen-address').value = values.listenAddress;
  document.querySelector('#listen-port').value = values.listenPort;
  document.querySelector('#remote-host').value = values.remoteHost;
  document.querySelector('#remote-port').value = values.remotePort;
  selectButtons('[data-role]', role);
  updateTunnelCommand();
}

function formatTunnelCommand(lines, shell) {
  const continuation = shell === 'powershell' ? '`' : shell === 'cmd' ? '^' : '\\';
  return lines.join(` ${continuation}\n  `);
}

function updateTunnelCommand() {
  const hasTls = tlsMode.value !== 'None';
  const tlsListener = hasTls && tunnelState.role === 'client';
  const tlsDestination = hasTls && tunnelState.role === 'server';
  const strictTls = tlsMode.value === 'SqlServerStrict';
  tlsProtocols.disabled = !hasTls || tlsMode.value === 'SqlServer';
  tlsCipher.disabled = !hasTls;
  tlsCertificate.disabled = !tlsListener;
  if (strictTls && tlsCertificate.value === 'generated') tlsCertificate.value = 'pfx';
  tlsCertificatePath.disabled = !tlsListener || tlsCertificate.value === 'generated';
  tlsKeyPath.disabled = !tlsListener || tlsCertificate.value !== 'pem';
  tlsName.disabled = !tlsListener || tlsCertificate.value !== 'generated';
  tlsTargetHost.disabled = !tlsDestination;
  tlsTrust.disabled = !tlsDestination || strictTls;
  if (strictTls) tlsTrust.checked = false;
  if (tlsMode.value === 'SqlServer') tlsProtocols.value = 'Tls12';
  if ((tlsProtocols.value === 'Tls12' && tlsCipher.value === 'TLS_AES_256_GCM_SHA384') ||
      (tlsProtocols.value === 'Tls13' && tlsCipher.value.startsWith('TLS_ECDHE_'))) tlsCipher.value = '';
  for (const [control, visible] of [
    [tlsProtocols, hasTls], [tlsCipher, hasTls], [tlsCertificate, tlsListener],
    [tlsCertificatePath, !tlsCertificatePath.disabled], [tlsKeyPath, !tlsKeyPath.disabled],
    [tlsName, !tlsName.disabled], [tlsTargetHost, tlsDestination], [tlsTrust, tlsDestination && !strictTls]
  ]) control.closest('label').hidden = !visible;

  keyId.disabled = encryption.value === 'None';
  compressionHistory.disabled = compression.value !== 'Brotli';
  if (compressionHistory.disabled) compressionHistory.checked = false;
  commandStatus.textContent = '';

  if (!commandForm.checkValidity()) {
    commandOutput.textContent = 'Complete the highlighted fields to generate a command.';
    commandCopy.disabled = true;
    return;
  }

  const mode = tunnelState.role === 'client' ? 'Client' : 'Server';
  const lines = [
    `tcptunnel --forward ${document.querySelector('#forward-name').value} --mode ${mode}`,
    `--listen-address ${document.querySelector('#listen-address').value} --listen-port ${document.querySelector('#listen-port').value}`,
    `--remote-host ${document.querySelector('#remote-host').value} --remote-port ${document.querySelector('#remote-port').value}`,
    `--compression ${compression.value} --batch-milliseconds ${document.querySelector('#batch-milliseconds').value}`,
    compressionHistory.checked ? '--compression-history true' : '',
    encryption.value !== 'None' ? `--encryption:algorithm ${encryption.value}` : '',
    encryption.value === 'None' ? '' : tunnelState.role === 'client'
      ? `--encryption:key-id ${keyId.value} --encryption:key REPLACE_WITH_GENERATED_KEY`
      : `--encryption:keys:${keyId.value} REPLACE_WITH_GENERATED_KEY`
  ].filter(Boolean);

  if (hasTls) {
    const prefix = tlsListener ? 'listen-tls' : 'remote-tls';
    lines.push(`--${prefix}:mode ${tlsMode.value}`);
    lines.push(`--${prefix}:protocols "${tlsMode.value === 'SqlServer' ? 'Tls12' : tlsProtocols.value}"`);
    if (tlsCipher.value) lines.push(`--${prefix}:cipher-suites ${tlsCipher.value}`);
    if (tlsListener) {
      if (tlsCertificate.value === 'generated') lines.push(`--listen-tls:generate-self-signed --listen-tls:self-signed-name ${tlsName.value}`);
      else {
        // File fields permit only literal path characters, excluding shell expansion.
        lines.push(`--listen-tls:certificate-path "${tlsCertificatePath.value}"`);
        if (tlsCertificate.value === 'pem') lines.push(`--listen-tls:certificate-key-path "${tlsKeyPath.value}"`);
      }
    } else {
      if (tlsTargetHost.value) lines.push(`--remote-tls:target-host ${tlsTargetHost.value}`);
      if (tlsTrust.checked) lines.push('--remote-tls:trust-server-certificate');
    }
  }

  commandOutput.textContent = formatTunnelCommand(lines, tunnelState.shell);
  commandCopy.disabled = false;
  document.querySelector('#command-label').textContent = `${mode.toUpperCase()} · ${tunnelState.shell === 'cmd' ? 'COMMAND PROMPT' : tunnelState.shell.toUpperCase()}`;
  document.querySelector('#command-note').textContent = tunnelState.role === 'client'
    ? 'Run this near the application. It listens locally and connects to the tunnel server.'
    : 'Run this near the destination. Permit the listen port through the network firewall, and use identical compression and encryption settings on the client.';
  if (encryption.value !== 'None') document.querySelector('#command-note').textContent += ' Run tcptunnel --generate-key locally and replace REPLACE_WITH_GENERATED_KEY on both peers with the same output. Use a different key for each client. For production, store keys in a restricted JSON file; command-line keys appear in shell history and process listings.';
  if (hasTls) {
    document.querySelector('#command-note').textContent += ' TLS terminates at the application endpoints so compression receives decrypted data. Protect the intervening link with shared-key tunnel encryption.';
    if (tlsMode.value === 'SqlServer') document.querySelector('#command-note').textContent += ' Select SQL Server TDS 7.x on both tunnel ends. Connect the SQL driver to the local listen port with Encrypt=True.';
    if (strictTls) document.querySelector('#command-note').textContent += ' Encrypt=Strict requires the application to trust the listener certificate and the tunnel server to validate the destination certificate.';
    if (tlsListener && tlsCertificate.value === 'generated') document.querySelector('#command-note').textContent += ' The self-signed certificate changes on restart. For a persistent certificate, run tcptunnel --generate-certificate client.pfx and select that file.';
    if (tlsTrust.checked && tlsDestination) document.querySelector('#command-note').textContent += ' Trusting an invalid certificate encrypts traffic without authenticating the destination.';
    if (tlsCipher.value) document.querySelector('#command-note').textContent += ' Explicit cipher suites require Linux; Windows uses Schannel OS policy.';
    if (tlsListener && tlsCertificate.value !== 'generated') document.querySelector('#command-note').textContent += ' For a password-protected key, set ListenTls.CertificatePassword in a restricted JSON file.';
  }
}

for (const button of document.querySelectorAll('[data-role]')) {
  button.addEventListener('click', () => applyRole(button.dataset.role));
}
for (const button of document.querySelectorAll('[data-shell]')) {
  button.addEventListener('click', () => {
    tunnelState.shell = button.dataset.shell;
    selectButtons('[data-shell]', tunnelState.shell);
    updateTunnelCommand();
  });
}
commandForm.addEventListener('input', updateTunnelCommand);
commandForm.addEventListener('change', updateTunnelCommand);

function flattenedCommand(command) {
  return command.replace(/[\\`^]\r?\n\s*/g, ' ').replace(/\r?\n\s*/g, ' ');
}

async function copyText(text, status, flatten = false) {
  const value = flatten ? flattenedCommand(text) : text;
  try {
    if (!navigator.clipboard || typeof navigator.clipboard.writeText !== 'function') throw new Error('Clipboard API unavailable');
    await navigator.clipboard.writeText(value);
    status.textContent = 'Command copied.';
  } catch {
    const input = document.createElement('textarea');
    input.value = value;
    input.setAttribute('readonly', '');
    input.style.position = 'fixed';
    input.style.opacity = '0';
    document.body.appendChild(input);
    input.select();
    let copied = false;
    try { copied = document.execCommand('copy'); }
    catch { copied = false; }
    input.remove();
    status.textContent = copied ? 'Command copied.' : 'Select the command text to copy it manually.';
  }
}

commandCopy.addEventListener('click', () => copyText(commandOutput.textContent, commandStatus, true));

const installState = { shell: 'powershell', assets: new Map() };
const installPlatform = document.querySelector('#install-platform');
const installArchitecture = document.querySelector('#install-architecture');
const installOutput = document.querySelector('#install-command');
const installCopy = document.querySelector('#copy-install');
const installStatus = document.querySelector('#install-copy-status');

function trustedAsset(asset) {
  try {
    const url = new URL(asset.browser_download_url);
    return url.protocol === 'https:' &&
      url.hostname === 'github.com' &&
      url.pathname.startsWith(`/${repository}/releases/download/`) &&
      /^[A-Za-z0-9._-]+$/.test(asset.name)
      ? { name: asset.name, url: url.href }
      : null;
  } catch {
    return null;
  }
}

function releaseResolvingInstallCommand(platform, shell, architecture) {
  const releaseEndpoint = `https://api.github.com/repos/${repository}/releases/latest`;
  const assetPattern = platform === 'windows'
    ? `*-win-${architecture}-setup.exe`
    : `*-linux-${architecture}.zip`;

  if (shell === 'powershell' && platform === 'windows') {
    return `$release = Invoke-RestMethod '${releaseEndpoint}'; $asset = $release.assets | Where-Object name -Like '${assetPattern}' | Select-Object -First 1; if (!$asset) { throw 'No compatible installer is present in the latest release.' }; $installer = Join-Path $env:TEMP $asset.name; $ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installer; Start-Process -FilePath $installer -Wait; Remove-Item -LiteralPath $installer`;
  }
  if (shell === 'cmd') {
    return `powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $release=Invoke-RestMethod '${releaseEndpoint}'; $asset=$release.assets | Where-Object name -Like '${assetPattern}' | Select-Object -First 1; if(!$asset){throw 'No compatible installer is present in the latest release.'}; $installer=Join-Path $env:TEMP $asset.name; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installer; Start-Process -FilePath $installer -Wait; Remove-Item -LiteralPath $installer"`;
  }
  if (shell === 'powershell') {
    return `$release = Invoke-RestMethod '${releaseEndpoint}'; $asset = $release.assets | Where-Object name -Like '${assetPattern}' | Select-Object -First 1; if (!$asset) { throw 'No compatible package is present in the latest release.' }; $archive = Join-Path ([IO.Path]::GetTempPath()) $asset.name; $installRoot = Join-Path $HOME '.local/share/tcptunnel'; $binDir = Join-Path $HOME '.local/bin'; $ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive; New-Item -ItemType Directory -Force -Path $installRoot, $binDir | Out-Null; Expand-Archive -LiteralPath $archive -DestinationPath $installRoot -Force; & chmod +x (Join-Path $installRoot 'tcptunnel'); & ln -sfn (Join-Path $installRoot 'tcptunnel') (Join-Path $binDir 'tcptunnel'); Remove-Item -LiteralPath $archive`;
  }

  const distribution = platform === 'windows'
    ? `win-${architecture}-setup.exe`
    : `linux-${architecture}.zip`;
  if (platform === 'windows') {
    return `release_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' https://github.com/${repository}/releases/latest)" && version="\${release_url##*/v}" && installer="$(mktemp --suffix=.exe)" && curl -fL "https://github.com/${repository}/releases/download/v$version/tcptunnel-$version-${distribution}" -o "$installer" && "$installer" && rm -f "$installer"`;
  }
  return `release_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' https://github.com/${repository}/releases/latest)" && version="\${release_url##*/v}" && archive="$(mktemp --suffix=.zip)" && install_root="\${XDG_DATA_HOME:-$HOME/.local/share}/tcptunnel" && bin_dir="$HOME/.local/bin" && curl -fL "https://github.com/${repository}/releases/download/v$version/tcptunnel-$version-${distribution}" -o "$archive" && mkdir -p "$install_root" "$bin_dir" && unzip -oq "$archive" -d "$install_root" && chmod +x "$install_root/tcptunnel" && ln -sfn "$install_root/tcptunnel" "$bin_dir/tcptunnel" && rm -f "$archive"`;
}

function installCommand(asset, platform, shell, architecture) {
  if (!asset) return releaseResolvingInstallCommand(platform, shell, architecture);
  if (platform === 'windows' && shell === 'powershell') {
    return `$installer = Join-Path $env:TEMP '${asset.name}'; $ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri '${asset.url}' -OutFile $installer; Start-Process -FilePath $installer -Wait; Remove-Item -LiteralPath $installer`;
  }
  if (platform === 'windows' && shell === 'cmd') {
    return `curl.exe -fL "${asset.url}" -o "%TEMP%\\${asset.name}" && start /wait "" "%TEMP%\\${asset.name}" && del "%TEMP%\\${asset.name}"`;
  }
  if (platform === 'windows') {
    return `installer="$(mktemp --suffix=.exe)" && curl -fL '${asset.url}' -o "$installer" && "$installer" && rm -f "$installer"`;
  }
  if (shell === 'powershell') {
    return `$archive = Join-Path ([IO.Path]::GetTempPath()) '${asset.name}'; $installRoot = Join-Path $HOME '.local/share/tcptunnel'; $binDir = Join-Path $HOME '.local/bin'; $ErrorActionPreference = 'Stop'; Invoke-WebRequest -Uri '${asset.url}' -OutFile $archive; New-Item -ItemType Directory -Force -Path $installRoot, $binDir | Out-Null; Expand-Archive -LiteralPath $archive -DestinationPath $installRoot -Force; & chmod +x (Join-Path $installRoot 'tcptunnel'); & ln -sfn (Join-Path $installRoot 'tcptunnel') (Join-Path $binDir 'tcptunnel'); Remove-Item -LiteralPath $archive`;
  }
  return `archive="$(mktemp --suffix=.zip)" && install_root="\${XDG_DATA_HOME:-$HOME/.local/share}/tcptunnel" && bin_dir="$HOME/.local/bin" && curl -fL '${asset.url}' -o "$archive" && mkdir -p "$install_root" "$bin_dir" && unzip -oq "$archive" -d "$install_root" && chmod +x "$install_root/tcptunnel" && ln -sfn "$install_root/tcptunnel" "$bin_dir/tcptunnel" && rm -f "$archive"`;
}

function updateInstallCommand() {
  const platform = installPlatform.value;
  const architecture = installArchitecture.value;
  const cmdButton = document.querySelector('[data-install-shell="cmd"]');
  cmdButton.disabled = platform === 'linux';
  cmdButton.setAttribute('aria-disabled', String(cmdButton.disabled));
  if (cmdButton.disabled && installState.shell === 'cmd') installState.shell = 'powershell';
  selectButtons('[data-install-shell]', installState.shell);

  const suffix = platform === 'windows'
    ? `-win-${architecture}-setup.exe`
    : `-linux-${architecture}.zip`;
  const asset = [...installState.assets.values()].find(item => item.name.endsWith(suffix));
  document.querySelector('#install-command-label').textContent =
    `${platform.toUpperCase()} · ${architecture.toUpperCase()} · ${installState.shell === 'cmd' ? 'COMMAND PROMPT' : installState.shell.toUpperCase()}`;
  document.querySelector('#install-note').textContent = platform === 'windows'
    ? 'The installer adds TcpTunnel to the system PATH. Open a new terminal before running it.'
    : 'The command installs under ~/.local/share and links tcptunnel into ~/.local/bin. Ensure ~/.local/bin is on PATH.';
  installStatus.textContent = '';
  installOutput.textContent = installCommand(asset, platform, installState.shell, architecture);
  installCopy.disabled = false;
}

for (const button of document.querySelectorAll('[data-install-shell]')) {
  button.addEventListener('click', () => {
    if (button.disabled) return;
    installState.shell = button.dataset.installShell;
    updateInstallCommand();
  });
}
installPlatform.addEventListener('change', updateInstallCommand);
installArchitecture.addEventListener('change', updateInstallCommand);
installCopy.addEventListener('click', () => copyText(installOutput.textContent, installStatus));

async function loadDownloads() {
  const status = document.querySelector('#release-status');
  const controller = typeof AbortController === 'function' ? new AbortController() : null;
  const timeout = controller ? setTimeout(() => controller.abort(), 8000) : null;
  try {
    const response = await fetch(`https://api.github.com/repos/${repository}/releases?per_page=10`, controller ? { signal: controller.signal } : {});
    if (!response.ok) throw new Error('GitHub unavailable');
    const releases = await response.json();
    const release = releases.find(item => !item.draft && Array.isArray(item.assets) && item.assets.some(asset => /^tcptunnel-.*-win-x64\.zip$/.test(asset.name)));
    if (!release) {
      status.textContent = 'Preview builds are available from the deploy branch on GitHub Actions.';
      updateInstallCommand();
      return;
    }
    status.textContent = `${release.tag_name}${release.prerelease ? ' · Preview release' : ''} · Choose your platform and package.`;
    for (const asset of release.assets) {
      const trusted = trustedAsset(asset);
      if (trusted) installState.assets.set(trusted.name, trusted);
    }
    for (const platform of ['win', 'linux']) {
      const container = document.querySelector(platform === 'win' ? '#windows-downloads' : '#linux-downloads');
      const links = [];
      for (const arch of ['x64', 'arm64']) {
        for (const kind of platform === 'win' ? ['.exe', '-setup.exe', '.msi', '.zip'] : ['.zip']) {
          const asset = [...installState.assets.values()].find(item => item.name.endsWith(`-${platform}-${arch}${kind}`));
          if (!asset) continue;
          const link = document.createElement('a');
          link.href = asset.url;
          link.target = '_blank';
          link.rel = 'noopener noreferrer';
          const label = kind === '.exe' ? 'Portable EXE' : kind === '-setup.exe' ? 'Installer EXE' : kind.slice(1).toUpperCase();
          link.textContent = `${arch === 'arm64' ? 'ARM64' : 'x64'} · ${label} ↓`;
          links.push(link);
        }
      }
      if (links.length) setChildren(container, links);
    }
    updateInstallCommand();
  } catch {
    status.textContent = 'Browse builds on GitHub. Release information is temporarily unavailable.';
    updateInstallCommand();
  } finally {
    if (timeout) clearTimeout(timeout);
  }
}

function renderBenchmarkSummary(container, results) {
  const rows = results.map(result => {
    const row = document.createElement('tr');
    const label = document.createElement('th');
    label.scope = 'row';
    label.textContent = result.name;
    const median = document.createElement('td');
    median.textContent = `${result.medianMiBPerSecond.toFixed(1)} MiB/s`;
    const peak = document.createElement('td');
    peak.textContent = `${result.bestMiBPerSecond.toFixed(1)} MiB/s`;
    row.append(label, median, peak);
    return row;
  });
  setChildren(container, rows);
}

function percentageDifference(value, baseline) {
  const difference = (value / baseline - 1) * 100;
  return `${difference >= 0 ? '+' : ''}${difference.toFixed(1)}%`;
}

function renderBenchmarkInterpretation(container, zstandardFast, uncompressed) {
  const values = [
    [
      'How does Zstandard -5 compare with uncompressed?',
      `Median ${zstandardFast.medianMiBPerSecond.toFixed(1)} vs ${uncompressed.medianMiBPerSecond.toFixed(1)} MiB/s (${percentageDifference(zstandardFast.medianMiBPerSecond, uncompressed.medianMiBPerSecond)})`,
      "This run's median determines the displayed rank; it does not establish a universal ordering."
    ],
    [
      'What does peak add?',
      `Peak ${zstandardFast.bestMiBPerSecond.toFixed(1)} vs ${uncompressed.bestMiBPerSecond.toFixed(1)} MiB/s (${percentageDifference(zstandardFast.bestMiBPerSecond, uncompressed.bestMiBPerSecond)})`,
      'Peak is one observation per profile and shows a transient ceiling rather than expected throughput.'
    ],
    [
      'Why can compression help on loopback?',
      'No external bandwidth cap',
      'Framing, memory copies, TCP buffers, and scheduling still have finite cost. A fast codec can offset its CPU cost by moving fewer bytes.'
    ],
    [
      'Which value should be compared?',
      'Median primary; peak secondary',
      'Median is less sensitive to scheduler and cache outliers. Neither value predicts a real network without representative data and conditions.'
    ]
  ];
  const rows = values.map(valuesForRow => {
    const row = document.createElement('tr');
    valuesForRow.forEach((value, index) => {
      const cell = document.createElement(index === 0 ? 'th' : 'td');
      if (index === 0) cell.scope = 'row';
      cell.textContent = value;
      row.appendChild(cell);
    });
    return row;
  });
  setChildren(container, rows);
}

function validBenchmarkResult(result) {
  return result && typeof result.name === 'string' && typeof result.codec === 'string'
    && typeof result.settings === 'string' && typeof result.dataSet === 'string'
    && Number.isFinite(result.bestMiBPerSecond) && result.bestMiBPerSecond > 0
    && Number.isFinite(result.medianMiBPerSecond) && result.medianMiBPerSecond > 0;
}

async function loadBenchmarks() {
  const status = document.querySelector('#benchmark-status');
  try {
    const response = await fetch('benchmarks.json');
    if (!response.ok) throw new Error('Benchmark data unavailable');
    const data = await response.json();
    const results = Array.isArray(data.results) ? data.results.filter(validBenchmarkResult) : [];
    if (!results.length) throw new Error('Benchmark data invalid');

    const plaintext = results.filter(result => !result.encryption || result.encryption === 'None');
    const fastestByCodec = [...plaintext.reduce((profiles, result) => {
      const current = profiles.get(result.codec);
      if (!current || result.medianMiBPerSecond > current.medianMiBPerSecond) profiles.set(result.codec, result);
      return profiles;
    }, new Map()).values()].sort((left, right) => right.medianMiBPerSecond - left.medianMiBPerSecond);
    const brotli = plaintext.filter(result => result.codec === 'Brotli')
      .sort((left, right) => right.medianMiBPerSecond - left.medianMiBPerSecond);
    renderBenchmarkSummary(document.querySelector('#algorithm-summary-body'), fastestByCodec);
    renderBenchmarkSummary(document.querySelector('#brotli-summary-body'), brotli);

    const uncompressed = plaintext.find(result => result.codec === 'None');
    const zstandardFast = results.find(result => result.name === 'Zstandard -5');
    if (!uncompressed || !zstandardFast) throw new Error('Comparison profiles unavailable');
    renderBenchmarkInterpretation(document.querySelector('#benchmark-interpretation-body'), zstandardFast, uncompressed);

    const ranked = [...results].sort((left, right) => right.medianMiBPerSecond - left.medianMiBPerSecond);
    const rows = ranked.map(result => {
      const row = document.createElement('tr');
      for (const value of [result.name, result.settings, result.dataSet, result.medianMiBPerSecond.toFixed(1), result.bestMiBPerSecond.toFixed(1)]) {
        const cell = document.createElement('td');
        cell.textContent = value;
        row.append(cell);
      }
      return row;
    });
    setChildren(document.querySelector('#benchmark-table-body'), rows);

    const measured = new Date(data.generatedAtUtc);
    const date = Number.isNaN(measured.valueOf()) ? 'Recorded run' : measured.toLocaleDateString(undefined, { dateStyle: 'medium' });
    const machine = data.machine || {};
    const iterations = data.methodology && data.methodology.iterations;
    status.textContent = `${date} · ${machine.Processor || 'Windows'} · ${machine.Framework || '.NET'} · ${iterations || 7} measured runs per profile`;
  } catch {
    status.textContent = 'Benchmark data is temporarily unavailable. See benchmarks.md for the complete recorded results.';
  }
}

updateTunnelCommand();
updateInstallCommand();
loadDownloads();
loadBenchmarks();

const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
if (reducedMotion || !('IntersectionObserver' in window)) {
  document.querySelectorAll('.reveal').forEach(element => element.classList.add('visible'));
} else {
  const revealObserver = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add('visible');
      revealObserver.unobserve(entry.target);
    }
  }, { threshold: 0.12 });
  document.querySelectorAll('.reveal').forEach(element => revealObserver.observe(element));
}
