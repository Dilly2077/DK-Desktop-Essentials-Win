(() => {
  'use strict';

  const rail = document.getElementById('right-rail');
  if (!rail) return;

  const svg = (paths) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths}</svg>`;
  const icons = {
    chip: svg('<rect x="7" y="7" width="10" height="10" rx="2"/><path d="M9 3v4m3-4v4m3-4v4M9 17v4m3-4v4m3-4v4M3 9h4m-4 3h4m-4 3h4m10-6h4m-4 3h4m-4 3h4"/>'),
    database: svg('<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>'),
    network: svg('<circle cx="12" cy="12" r="2"/><circle cx="5" cy="6" r="2"/><circle cx="19" cy="6" r="2"/><circle cx="5" cy="18" r="2"/><circle cx="19" cy="18" r="2"/><path d="m7 7.5 3.3 3M17 7.5l-3.3 3M7 16.5l3.3-3M17 16.5l-3.3-3"/>'),
    gamepad: svg('<path d="M7.2 8h9.6c2.2 0 3.8 1.5 4.2 3.6l.8 4.5c.4 2.3-2.3 3.7-3.8 2l-2-2.1H8l-2 2.1c-1.5 1.7-4.2.3-3.8-2l.8-4.5C3.4 9.5 5 8 7.2 8Z"/><path d="M7 11v4M5 13h4"/><circle cx="16.5" cy="12" r=".8" fill="currentColor" stroke="none"/><circle cx="18.5" cy="14" r=".8" fill="currentColor" stroke="none"/>'),
    folder: svg('<path d="M3 6.5h7l2 2H21v10.5H3z"/>'),
    monitor: svg('<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8m-4-4v4"/>'),
    screen: svg('<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M8 21h8"/>'),
    printer: svg('<path d="M7 8V3h10v5M7 17H4v-7h16v7h-3M7 14h10v7H7z"/>'),
    pdf: svg('<path d="M6 3h8l4 4v14H6z"/><path d="M14 3v5h5M8 13h8M8 16h6"/>'),
    image: svg('<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m5 18 5-5 3 3 2-2 4 4"/>'),
    ocr: svg('<path d="M8 4H4v4m12-4h4v4M8 20H4v-4m12 4h4v-4"/><path d="M8 14V9h2.5a2.5 2.5 0 0 1 0 5H8Zm6-5v5m0-5h4"/>'),
    transfer: svg('<path d="M4 7h14m0 0-4-4m4 4-4 4M20 17H6m0 0 4-4m-4 4 4 4"/>')
  };

  const workspaceMarkup = `
    <div class="workspace-rail-shell" aria-label="Workspace overview">
      <section class="workspace-rail-card workspace-stats-card">
        <div class="workspace-rail-header">
          <span class="workspace-rail-icon blue">${icons.chip}</span>
          <div><strong>System Stats</strong><small>Hardware module placeholder</small></div>
        </div>
        <div class="mini-metric-grid">
          <div class="mini-metric"><span>CPU</span><b>—</b><i></i></div>
          <div class="mini-metric"><span>GPU</span><b>—</b><i></i></div>
          <div class="mini-metric"><span>RAM</span><b>—</b><i></i></div>
          <div class="mini-metric"><span>Disk</span><b>—</b><i></i></div>
        </div>
      </section>

      <section class="workspace-rail-card">
        <div class="workspace-rail-header">
          <span class="workspace-rail-icon cyan">${icons.database}</span>
          <div><strong>Driver Status</strong><small>Driver module not connected</small></div>
        </div>
        <div class="driver-summary-compact">
          <div class="driver-ring"><span>—</span><small>Drivers</small></div>
          <div class="driver-lines">
            <div><span class="status-dot green"></span><span>Up to date</span><b>—</b></div>
            <div><span class="status-dot amber"></span><span>Updates</span><b>—</b></div>
            <div><span class="status-dot slate"></span><span>Issues</span><b>—</b></div>
          </div>
        </div>
      </section>

      <section class="workspace-rail-card">
        <div class="workspace-rail-header">
          <span class="workspace-rail-icon green">${icons.network}</span>
          <div><strong>Network Devices</strong><small>Network scan not active</small></div>
        </div>
        <div class="workspace-device-list">
          <div><span class="device-icon">${icons.monitor}</span><span>This PC</span><small>Local device</small></div>
          <div class="muted"><span class="device-icon">${icons.screen}</span><span>Device</span><small>Waiting for scan</small></div>
          <div class="muted"><span class="device-icon">${icons.printer}</span><span>Device</span><small>Waiting for scan</small></div>
        </div>
      </section>

      <section class="workspace-rail-card controller-compact-card">
        <div class="workspace-rail-header">
          <span class="workspace-rail-icon blue">${icons.gamepad}</span>
          <div><strong>Controller</strong><small>Controller tools placeholder</small></div>
        </div>
        <div class="controller-compact-body">
          <span class="controller-large-icon">${icons.gamepad}</span>
          <div><strong>No controller data yet</strong><small>Input module will populate this panel later.</small></div>
        </div>
      </section>

      <section class="workspace-rail-card file-tools-card">
        <div class="workspace-rail-header">
          <span class="workspace-rail-icon purple">${icons.folder}</span>
          <div><strong>File Tools</strong><small>Quick access area</small></div>
        </div>
        <div class="workspace-file-grid">
          <div><span>${icons.pdf}</span><small>PDF</small></div>
          <div><span>${icons.image}</span><small>Images</small></div>
          <div><span>${icons.ocr}</span><small>OCR</small></div>
          <div><span>${icons.transfer}</span><small>Transfer</small></div>
        </div>
      </section>
    </div>`;

  let replacing = false;
  const applyWorkspaceRail = () => {
    if (replacing) return;
    if (rail.querySelector('.workspace-rail-shell')) return;
    replacing = true;
    rail.innerHTML = workspaceMarkup;
    replacing = false;
  };

  const observer = new MutationObserver(() => applyWorkspaceRail());
  observer.observe(rail, { childList: true, subtree: false });

  applyWorkspaceRail();
})();
