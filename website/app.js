const repository = 'tedd/Tedd.TcpTunnel';
const profiles = {
  latency: { command: 'tcptunnel --forward app --mode Client\n  --listen-port 9000 --remote-host tunnel.example\n  --remote-port 9001 --compression Lz4\n  --batch-milliseconds 0 --socket:no-delay true', note: 'LZ4 with immediate forwarding and TCP_NODELAY. Run a matching Server on the other end.' },
  throughput: { command: 'tcptunnel --forward app --mode Client\n  --listen-port 9000 --remote-host tunnel.example\n  --remote-port 9001 --compression Zstandard\n  --buffer-size 262144 --batch-milliseconds 2\n  --execution Dedicated', note: 'Larger buffers and a short batching window. Dedicated mode uses two OS threads per connection; benchmark it against Async for your workload.' },
  compression: { command: 'tcptunnel --forward app --mode Client\n  --listen-port 9000 --remote-host tunnel.example\n  --remote-port 9001 --compression Brotli\n  --brotli-quality 9 --brotli-window 22\n  --compression-history true --batch-milliseconds 10', note: 'Brotli history reuses content across frames. Higher quality spends more CPU to save bandwidth. Match compression and history settings on the Server.' }
};
for (const button of document.querySelectorAll('[data-profile]')) button.addEventListener('click', () => {
  const profile = profiles[button.dataset.profile];
  document.querySelector('#command').textContent = profile.command;
  document.querySelector('#profile-note').textContent = profile.note;
  document.querySelector('#copy-status').textContent = '';
  for (const other of document.querySelectorAll('[data-profile]')) other.setAttribute('aria-pressed', String(other === button));
});
document.querySelector('#copy').addEventListener('click', async () => {
  try {
    await navigator.clipboard.writeText(document.querySelector('#command').textContent.replace(/\n\s*/g, ' '));
    document.querySelector('#copy-status').textContent = 'Command copied.';
  } catch { document.querySelector('#copy-status').textContent = 'Select the command text to copy it manually.'; }
});
async function loadDownloads() {
  const status = document.querySelector('#release-status');
  try {
    const response = await fetch(`https://api.github.com/repos/${repository}/releases?per_page=10`, { signal: AbortSignal.timeout(8000) });
    if (!response.ok) throw new Error('GitHub unavailable');
    const releases = await response.json();
    const release = releases.find(item => !item.draft && Array.isArray(item.assets) && item.assets.some(asset => /^tcptunnel-.*-win-x64\.zip$/.test(asset.name)));
    if (!release) { status.textContent = 'Preview builds are available from the deploy branch on GitHub Actions.'; return; }
    status.textContent = `${release.tag_name}${release.prerelease ? ' · Preview release' : ''} · Choose your platform and package.`;
    for (const platform of ['win', 'linux']) {
      const container = document.querySelector(platform === 'win' ? '#windows-downloads' : '#linux-downloads');
      const links = [];
      for (const arch of ['x64', 'arm64']) for (const kind of platform === 'win' ? ['.msi', '-setup.exe', '.zip'] : ['.zip']) {
        const asset = release.assets.find(item => item.name.endsWith(`-${platform}-${arch}${kind}`));
        if (!asset) continue;
        const url = new URL(asset.browser_download_url);
        if (url.protocol !== 'https:' || url.hostname !== 'github.com' || !url.pathname.startsWith(`/${repository}/releases/download/`)) continue;
        const link = document.createElement('a'); link.href = url.href;
        link.textContent = `${arch === 'arm64' ? 'ARM64' : 'x64'} · ${kind === '-setup.exe' ? 'EXE' : kind.slice(1).toUpperCase()} ↓`;
        links.push(link);
      }
      if (links.length) container.replaceChildren(...links);
    }
  } catch { status.textContent = 'Browse builds on GitHub. Release information is temporarily unavailable.'; }
}
loadDownloads();
