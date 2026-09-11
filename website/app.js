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

function selectButtons(selector, selected) {
  for (const button of document.querySelectorAll(selector)) {
    button.setAttribute('aria-pressed', String(button.dataset.role === selected || button.dataset.shell === selected || button.dataset.installShell === selected));
  }
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
    compressionHistory.checked ? '--compression-history true' : ''
  ].filter(Boolean);

  commandOutput.textContent = formatTunnelCommand(lines, tunnelState.shell);
  commandCopy.disabled = false;
  document.querySelector('#command-label').textContent = `${mode.toUpperCase()} · ${tunnelState.shell === 'cmd' ? 'COMMAND PROMPT' : tunnelState.shell.toUpperCase()}`;
  document.querySelector('#command-note').textContent = tunnelState.role === 'client'
    ? 'Run this near the application. It listens locally and connects to the tunnel server.'
    : 'Run this near the destination. Permit the listen port through the network firewall, and use identical compression settings on the client.';
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
  try {
    await navigator.clipboard.writeText(flatten ? flattenedCommand(text) : text);
    status.textContent = 'Command copied.';
  } catch {
    status.textContent = 'Select the command text to copy it manually.';
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
  try {
    const response = await fetch(`https://api.github.com/repos/${repository}/releases?per_page=10`, { signal: AbortSignal.timeout(8000) });
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
        for (const kind of platform === 'win' ? ['.msi', '-setup.exe', '.zip'] : ['.zip']) {
          const asset = [...installState.assets.values()].find(item => item.name.endsWith(`-${platform}-${arch}${kind}`));
          if (!asset) continue;
          const link = document.createElement('a');
          link.href = asset.url;
          link.textContent = `${arch === 'arm64' ? 'ARM64' : 'x64'} · ${kind === '-setup.exe' ? 'EXE' : kind.slice(1).toUpperCase()} ↓`;
          links.push(link);
        }
      }
      if (links.length) container.replaceChildren(...links);
    }
    updateInstallCommand();
  } catch {
    status.textContent = 'Browse builds on GitHub. Release information is temporarily unavailable.';
    updateInstallCommand();
  }
}

function renderBenchmarkChart(container, results) {
  const maximum = Math.max(...results.map(result => result.bestMiBPerSecond));
  const rows = results.map(result => {
    const row = document.createElement('div');
    row.className = 'benchmark-row';

    const label = document.createElement('span');
    label.className = 'benchmark-label';
    label.textContent = result.name;

    const track = document.createElement('span');
    track.className = 'benchmark-track';
    const bar = document.createElement('i');
    const percentage = maximum > 0 ? Math.max(1, Math.min(100, result.bestMiBPerSecond / maximum * 100)) : 0;
    bar.style.width = `${percentage}%`;
    track.append(bar);

    const value = document.createElement('strong');
    value.textContent = `${result.bestMiBPerSecond.toFixed(1)} MiB/s`;
    row.append(label, track, value);
    return row;
  });
  container.replaceChildren(...rows);
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

    const fastestByCodec = [...results.reduce((profiles, result) => {
      const current = profiles.get(result.codec);
      if (!current || result.bestMiBPerSecond > current.bestMiBPerSecond) profiles.set(result.codec, result);
      return profiles;
    }, new Map()).values()].sort((left, right) => right.bestMiBPerSecond - left.bestMiBPerSecond);
    const brotli = results.filter(result => result.codec === 'Brotli');
    renderBenchmarkChart(document.querySelector('#algorithm-chart'), fastestByCodec);
    renderBenchmarkChart(document.querySelector('#brotli-chart'), brotli);

    const ranked = [...results].sort((left, right) => right.bestMiBPerSecond - left.bestMiBPerSecond);
    const rows = ranked.map(result => {
      const row = document.createElement('tr');
      for (const value of [result.name, result.settings, result.dataSet, result.bestMiBPerSecond.toFixed(1), result.medianMiBPerSecond.toFixed(1)]) {
        const cell = document.createElement('td');
        cell.textContent = value;
        row.append(cell);
      }
      return row;
    });
    document.querySelector('#benchmark-table-body').replaceChildren(...rows);

    const measured = new Date(data.generatedAtUtc);
    const date = Number.isNaN(measured.valueOf()) ? 'Recorded run' : measured.toLocaleDateString(undefined, { dateStyle: 'medium' });
    const machine = data.machine ?? {};
    const iterations = data.methodology?.iterations;
    status.textContent = `${date} · ${machine.Processor ?? 'Windows'} · ${machine.Framework ?? '.NET'} · ${iterations ?? 3} measured runs per profile`;
  } catch {
    status.textContent = 'Benchmark data is temporarily unavailable. See benchmarks.md for the complete recorded results.';
  }
}

updateTunnelCommand();
updateInstallCommand();
loadDownloads();
loadBenchmarks();
