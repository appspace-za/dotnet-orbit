// JavaScript for the Blazor pages (Components/), called through IJSRuntime as orbit.*. The Razor layout (Identity and the
// error page) has its own few lines for the theme toggle.
(function () {
  var THEME = 'orbit-theme';

  // The theme lives in localStorage, as the Razor pages have always kept it, and in a cookie, which lets the server
  // colour a Blazor page before its circuit connects (App.razor).
  function remember(theme) {
    try { localStorage.setItem(THEME, theme); } catch (e) { /* storage unavailable */ }
    var secure = location.protocol === 'https:' ? '; secure' : '';
    document.cookie = THEME + '=' + theme + '; path=/; max-age=31536000; samesite=lax' + secure;
  }

  function apply(theme) {
    document.documentElement.setAttribute('data-theme', theme);
    document.body.classList.toggle('orbit-dark', theme === 'dark');
  }

  window.orbit = {
    theme: {
      // The saved choice, then the OS preference, then light. Remembered as a cookie from now on.
      current: function () {
        var theme = null;
        try { theme = localStorage.getItem(THEME); } catch (e) { }
        if (theme !== 'light' && theme !== 'dark') {
          theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        }
        remember(theme);
        apply(theme);
        return theme;
      },
      set: function (theme) {
        remember(theme);
        apply(theme);
      }
    }
  };

  // The task clock (spec §6.10, TaskClock.razor). While it runs on a task page this ticks the elapsed time every second, and
  // checks in with the server every heartbeat through the page's connection (a lost connection means no check-ins, so the
  // server can tell the clock's page is gone and stop it at its last one). Leaving the page with a full page load stops the
  // clock with navigator.sendBeacon, so the time is logged; TaskClock stops it itself when the user moves to another Blazor page.
  window.orbit.clock = {
    watch: function (display, startedIso, heartbeatSeconds, beaconUrl, token, dotnet) {
      var startedAt = new Date(startedIso).getTime();
      function pad(n) { return (n < 10 ? '0' : '') + n; }
      function tick() {
        var s = Math.max(0, Math.floor((Date.now() - startedAt) / 1000));
        if (display) display.textContent = pad(Math.floor(s / 3600)) + ':' + pad(Math.floor((s % 3600) / 60)) + ':' + pad(s % 60);
      }
      var beating = false;
      function beat() {
        if (beating) return;
        beating = true;
        dotnet.invokeMethodAsync('HeartbeatAsync')
          .catch(function () { /* no connection: no check-in, as intended */ })
          .then(function () { beating = false; });
      }
      function visible() { if (document.visibilityState === 'visible') beat(); }
      function leave() {
        if (!navigator.sendBeacon) return;
        var form = new FormData();
        form.append('__RequestVerificationToken', token);
        navigator.sendBeacon(beaconUrl, form);
      }
      // Coming back through the back/forward cache would show a clock that was already stopped: reload instead.
      function shown(e) { if (e.persisted) location.reload(); }

      tick();
      var ticker = setInterval(tick, 1000);
      var heart = setInterval(beat, (heartbeatSeconds || 60) * 1000);
      // A background tab's timers may be slowed or paused: check in as soon as it is shown again.
      document.addEventListener('visibilitychange', visible);
      window.addEventListener('pagehide', leave);
      window.addEventListener('pageshow', shown);
      return {
        dispose: function () {
          clearInterval(ticker);
          clearInterval(heart);
          document.removeEventListener('visibilitychange', visible);
          window.removeEventListener('pagehide', leave);
          window.removeEventListener('pageshow', shown);
        }
      };
    }
  };

  // Gantt drag-to-reschedule (spec §6.16, ProjectGantt.razor). On a row the viewer may edit (data-editable): drag a bar to move
  // both dates, drag either end to change the start or the due date alone (an end dragged on a start-only bar gives it a due
  // date), or drag a milestone to move its due date. Moves snap to whole days and a label shows the dates while dragging;
  // releasing calls the page's RescheduleAsync, and a refused change puts the bar back. Only the dragged task moves.
  window.orbit.gantt = {
    attach: function (canvas, label, px, dotnet) {
      px = px || 24;
      var EDGE = 7;
      var MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
      var drag = null;

      function parse(iso) { return iso ? new Date(iso + 'T00:00:00Z') : null; }
      function toIso(d) { return d ? d.toISOString().slice(0, 10) : ''; }
      function addDays(iso, days) { var d = parse(iso); if (!d) return ''; d.setUTCDate(d.getUTCDate() + days); return toIso(d); }
      function pretty(iso) { var d = parse(iso); return d ? d.getUTCDate() + ' ' + MONTHS[d.getUTCMonth()] : '?'; }
      function spanDays(start, end) { var a = parse(start), b = parse(end); return a && b ? Math.round((b - a) / 86400000) : 0; }

      // Where on the mark the pointer is: an end (resize) or the body (move). Milestones only move.
      function modeAt(el, clientX) {
        if (el.classList.contains('gantt-milestone')) return 'move';
        var r = el.getBoundingClientRect();
        if (r.width > 3 * EDGE) {
          if (clientX - r.left <= EDGE) return 'start';
          if (r.right - clientX <= EDGE) return 'end';
        }
        return 'move';
      }
      function tentative(d, days) {
        var s = d.start, e = d.end;
        if (d.mode === 'move') { s = addDays(s, days); e = addDays(e, days); }
        else if (d.mode === 'start') { s = addDays(s, days); }
        else { e = addDays(e || d.start, days); }
        return { start: s, end: e };
      }
      // The start can't pass the due date and vice versa.
      function clamp(d, days) {
        var span = spanDays(d.start, d.end);
        if (d.mode === 'start' && d.end) return Math.min(days, span);
        if (d.mode === 'end') return Math.max(days, d.end ? -span : 0);
        return days;
      }
      function render(d, days) {
        var el = d.el;
        if (d.mode === 'move') { el.style.left = (d.left + days * px) + 'px'; }
        else if (d.mode === 'start') { el.style.left = (d.left + days * px) + 'px'; el.style.width = Math.max(4, d.width - days * px) + 'px'; }
        else { el.style.width = Math.max(4, d.width + days * px) + 'px'; }
        if (!label) return;
        var t = tentative(d, days);
        label.textContent = t.start && t.end ? pretty(t.start) + ' → ' + pretty(t.end) : t.start ? 'starts ' + pretty(t.start) : 'due ' + pretty(t.end);
        label.style.left = el.offsetLeft + 'px';
        label.style.top = (el.offsetTop - 22) + 'px';
        label.hidden = false;
      }
      function putBack(d) { d.el.style.left = d.left + 'px'; d.el.style.width = d.width + 'px'; }

      function move(e) {
        if (drag) {
          drag.days = clamp(drag, Math.round((e.clientX - drag.x0) / px));
          render(drag, drag.days);
          return;
        }
        var el = e.target.closest('[data-editable="true"]');
        if (el) el.style.cursor = modeAt(el, e.clientX) === 'move' ? 'grab' : 'ew-resize';
      }
      function down(e) {
        var el = e.target.closest('[data-editable="true"]');
        if (!el || e.button !== 0) return;
        e.preventDefault();
        drag = {
          el: el, mode: modeAt(el, e.clientX), x0: e.clientX, left: el.offsetLeft, width: el.offsetWidth,
          start: el.dataset.start || '', end: el.dataset.end || '', days: 0
        };
        el.classList.add('gantt-dragging');
        try { el.setPointerCapture(e.pointerId); } catch (err) { /* pointer capture is optional */ }
      }
      function finish() {
        if (!drag) return;
        var d = drag;
        drag = null;
        d.el.classList.remove('gantt-dragging');
        if (label) label.hidden = true;
        if (d.days === 0) { putBack(d); return; }
        var t = tentative(d, d.days);
        dotnet.invokeMethodAsync('RescheduleAsync', d.el.dataset.task, t.start || null, t.end || null)
          .then(function (saved) { if (!saved) putBack(d); })
          .catch(function () { putBack(d); });
      }

      canvas.addEventListener('pointermove', move);
      canvas.addEventListener('pointerdown', down);
      canvas.addEventListener('pointerup', finish);
      canvas.addEventListener('pointercancel', finish);
      return {
        dispose: function () {
          canvas.removeEventListener('pointermove', move);
          canvas.removeEventListener('pointerdown', down);
          canvas.removeEventListener('pointerup', finish);
          canvas.removeEventListener('pointercancel', finish);
        }
      };
    }
  };

  // A list page's remembered filters (spec §6.2, BrowserFilterMemory): the last filter used, per page, kept for the browser
  // session in a cookie (no expiry) tied to the signed-in user. The Razor pages keep theirs in an HttpOnly cookie the server
  // sets; a Blazor page has no response of its own, so it sets one here, under its own name.
  function readCookie(name) {
    var parts = document.cookie ? document.cookie.split('; ') : [];
    for (var i = 0; i < parts.length; i++) {
      var eq = parts[i].indexOf('=');
      if (parts[i].slice(0, eq) === name) return decodeURIComponent(parts[i].slice(eq + 1));
    }
    return null;
  }
  window.orbit.filters = {
    recall: function (page, owner) {
      var raw = readCookie('orbit-filters-' + page);
      if (!raw) return null;
      var split = raw.indexOf('|');
      if (split < 0 || raw.slice(0, split) !== owner) return null;
      return raw.slice(split + 1) || null;
    },
    remember: function (page, owner, query) {
      if (!query) { window.orbit.filters.forget(page); return; }
      var secure = location.protocol === 'https:' ? '; secure' : '';
      document.cookie = 'orbit-filters-' + page + '=' + encodeURIComponent(owner + '|' + query) + '; path=/; samesite=lax' + secure;
    },
    forget: function (page) {
      document.cookie = 'orbit-filters-' + page + '=; path=/; max-age=0; samesite=lax';
    }
  };

  // The token picker (TokenPicker.razor, §6.20): puts a token where the cursor is in the box with this id, and says the box's new
  // text and where the cursor goes after it; caret() puts it there once Blazor has drawn the new text.
  window.orbit.tokens = {
    insert: function (id, token) {
      var box = document.getElementById(id);
      if (!box) return null;
      var start = box.selectionStart == null ? box.value.length : box.selectionStart;
      var end = box.selectionEnd == null ? start : box.selectionEnd;
      return { value: box.value.slice(0, start) + token + box.value.slice(end), caret: start + token.length };
    },
    caret: function (id, at) {
      var box = document.getElementById(id);
      if (!box) return;
      box.focus();
      try { box.setSelectionRange(at, at); } catch (err) { /* not a text box */ }
    }
  };

  // A short low tone (the asset quick check, §6.19): a scan that needs a look - a choice to make, or a refusal.
  var audio = null;
  window.orbit.beep = function () {
    try {
      var Context = window.AudioContext || window.webkitAudioContext;
      if (!Context) return;
      audio = audio || new Context();
      if (audio.state === 'suspended') audio.resume();
      var tone = audio.createOscillator();
      var gain = audio.createGain();
      tone.type = 'square';
      tone.frequency.value = 220;
      gain.gain.value = 0.08;
      tone.connect(gain);
      gain.connect(audio.destination);
      tone.start();
      tone.stop(audio.currentTime + 0.3);
    } catch (err) { /* no audio: the log still shows it */ }
  };

  // Signing out, or in as someone else, in another tab (spec §8). A Blazor page keeps the user it was opened as for as long as
  // its connection lasts, and never sees the cookie change. So every Orbit page says who this browser is signed in as (its
  // orbit-user meta, empty when nobody is), and a Blazor page opened as anyone else reloads: to the sign-in page if nobody is
  // signed in now, or as the new user. The account layout (_Layout.cshtml) only announces; its next request carries the cookie.
  if ('BroadcastChannel' in window) {
    var meta = document.querySelector('meta[name="orbit-user"]');
    var me = meta ? meta.content : '';
    var session = new BroadcastChannel('orbit-session');
    session.onmessage = function (e) { if (e.data !== me) location.reload(); };
    session.postMessage(me);
  }

  // The reconnect dialog's buttons (App.razor).
  document.addEventListener('click', function (e) {
    if (e.target.closest('.js-reconnect-retry') && window.Blazor) window.Blazor.reconnect();
    if (e.target.closest('.js-reconnect-reload')) location.reload();
  });
})();
