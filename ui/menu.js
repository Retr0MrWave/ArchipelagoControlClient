(function () {
  "use strict";

  if (window.AP && window.AP.destroy) {
    try { window.AP.destroy(); } catch (e) { }
  }

  var send = window.APSend || function () {};

  var GLOBAL = window.__APCONTROL || (window.__APCONTROL = { handlers: [], seq: 0 });
  GLOBAL.seq = (GLOBAL.seq || 0) + 1;

  function getWebpackRequire() {
    var jsonp = window.webpackJsonp;
    if (!jsonp || typeof jsonp.push !== "function") return null;

    var req = null;
    var id = "ap/bridge/" + GLOBAL.seq + "/" + Date.now();
    var modules = {};
    modules[id] = function (module, exports, __webpack_require__) { req = __webpack_require__; };

    try {
      jsonp.push([[], modules, [[id]]]);
    } catch (e) {
      return null;
    }
    return req;
  }

  var require_ = getWebpackRequire();
  window.APRequire = require_;

  function rawRequire(path) {
    if (!require_) return null;
    try {
      return require_(path) || null;
    } catch (e) {
      return null;
    }
  }

  function tryRequire(path) {
    var m = rawRequire(path);
    return (m && m.default) || m || null;
  }

  var React = require_ ? tryRequire("./node_modules/react/index.js") : null;
  var Section = tryRequire("./northlight/LinkComponents/Section/Section.tsx");
  var SectionButton = tryRequire("./northlight/LinkComponents/Section/SectionButton.tsx");
  var EventButton = tryRequire("./northlight/LinkComponents/EventButton/EventButton.tsx");
  var TileTitle = tryRequire("./northlight/components/TileTitle/TileTitle.tsx");
  var coherent = tryRequire("northlight/vendor/coherent");
  var ReactDOM = tryRequire("./node_modules/react-dom/index.js");
  var linkCtxModule = rawRequire("./northlight/Link/LinkContext.tsx");
  var LinkContext = linkCtxModule && linkCtxModule.LinkContext;

  var canBuildMenu = !!(React && React.createElement && Section && SectionButton);
  var canReparent = !!(ReactDOM && ReactDOM.createPortal && LinkContext && LinkContext.Provider);
  var canBuildButtons = !!(canBuildMenu && EventButton && TileTitle && coherent);

  // --- styles --------------------------------------------------------------------------------

  var STYLE_ID = "ap-control-style";
  var style = document.getElementById(STYLE_ID);
  if (!style) {
    style = document.createElement("style");
    style.id = STYLE_ID;
    (document.head || document.documentElement).appendChild(style);
  }
  style.textContent = [
    ".unlocalized_text{color:inherit!important;background:none!important;text-decoration:none!important}",
    ".ap-section{position:absolute;left:6vw;top:14vh;right:6vw;bottom:6vh;color:#e8e8e8;",
    "font-family:Akzidenz-Grotesk Pro,Arial,sans-serif;font-size:16px;line-height:1.7}",
    ".ap-section--hidden{display:none}",
    ".ap-section h1{color:#e9000d;font-size:30px;margin-bottom:1vh;font-weight:bold}",
    ".ap-section dt{color:#8a8a8a;display:inline-block;width:9em}",
    ".ap-page-active .dlc-message{display:none!important}",
    ".ap-section__form{margin-top:3vh;width:30vw}",
    ".ap-section__actions{margin-top:4vh;width:26vw}",
    ".ap-section__hint{margin-top:3vh;color:#8a8a8a}"
  ].join("");

  // --- state ---------------------------------------------------------------------------------

  var state = {
    status: "Waiting for client",
    slot: "-",
    seed: "-",
    checks: null,
    pending: null
  };

  function statusRows() {
    var rows = [
      ["Status", state.status],
      ["Slot", state.slot],
      ["Seed", state.seed]
    ];
    if (state.checks) rows.push(["Checks", state.checks.found + " / " + state.checks.total]);
    if (state.pending) rows.push(["Waiting", state.pending + " item(s) — load a save"]);
    return rows;
  }

  // --- the menu entry ------------------------------------------------------------------------

  var AP_ROUTE = "AP_MENU";
  var patchedCreateElement = null;
  var originalCreateElement = null;

  var FIELDS = [
    { id: "host", label: "Server", value: "archipelago.gg" },
    { id: "port", label: "Port", value: "38281", digits: true },
    { id: "slot", label: "Slot Name", value: "" },
    { id: "password", label: "Password", value: "", secret: true }
  ];

  var ACTIONS = [
    { id: "clear", label: function () { return "Clear " + (fieldById(lastField) || { label: "Field" }).label; } },
    { id: "connect", label: "Connect" },
  ];

  var editing = null;
  // Which field "Clear" is about. It has to outlive `editing`, because navigation is suspended
  // while a field is being edited — reaching the button at all means having stopped editing first.
  var lastField = null;
  var MAX_FIELD = 64;


  var NAV_EVENTS = ["OnNavigateUp", "OnNavigateDown", "OnNavigateLeft", "OnNavigateRight"];

  var ACTIVATE_EVENTS = ["OnX"];

  var suspended = {};

  function emitterEvents() {
    if (coherent && coherent.events) return coherent.events;
    if (window.engine && window.engine.events) return window.engine.events;
    return null;
  }

  // Registering a no-op is what makes the name exist in the emitter map, which is what suspending
  // it later needs: an event nobody has ever listened to has no entry to swap out.
  function materializeHandlers(names) {
    if (!coherent || !coherent.on) return;
    names.forEach(function (n) { listen(n, function () {}); });
  }

  function suspendEvents(names) {
    var map = emitterEvents();
    if (!map) return false;
    names.forEach(function (n) {
      if (n in suspended) return;
      suspended[n] = map[n];
      map[n] = [];
    });
    return true;
  }

  function resumeEvents(names) {
    var map = emitterEvents();
    names.forEach(function (n) {
      if (!(n in suspended)) return;
      if (map) {
        if (suspended[n] === undefined) delete map[n];
        else map[n] = suspended[n];
      }
      delete suspended[n];
    });
  }

  function setEditing(id) {
    editing = id;
    if (id) { lastField = id; suspendEvents(NAV_EVENTS); }
    else resumeEvents(NAV_EVENTS);
  }

  var onPage = false;

  function enterPage() {
    if (onPage) return;
    onPage = true;
    suspendEvents(ACTIVATE_EVENTS);
    if (document.body) document.body.classList.add("ap-page-active");
  }

  function leavePage() {
    if (!onPage) return;
    onPage = false;
    setEditing(null);
    resumeEvents(ACTIVATE_EVENTS);
    if (document.body) document.body.classList.remove("ap-page-active");
    refreshMainMenu();
  }

  function fieldEvent(id) { return "ApControlField_" + id; }
  function actionEvent(id) { return "ApControlAction_" + id; }

  function fieldById(id) {
    for (var i = 0; i < FIELDS.length; i++) if (FIELDS[i].id === id) return FIELDS[i];
    return null;
  }

  function adoptSession(session) {
    if (!session) return;
    FIELDS.forEach(function (f) {
      if (editing === f.id) return;
      var value = session[f.id];
      if (typeof value === "string" && value.length) f.value = value;
    });
  }

  function shown(f) {
    var text = f.secret ? new Array(f.value.length + 1).join("*") : f.value;
    if (editing === f.id) return text + "_";
    return text || "-";
  }

  function label(text) {
    return React.createElement(TileTitle, { locId: text, disableLocalisation: true });
  }

  function actionLabel(a) {
    return typeof a.label === "function" ? a.label() : a.label;
  }

  function apPage() {
    var rows = statusRows().map(function (r, i) {
      return React.createElement("div", { key: "row" + i },
        React.createElement("dt", null, r[0]),
        React.createElement("dd", { style: { display: "inline" } }, r[1]));
    });

    var children = [
      React.createElement("h1", { key: "h" }, "Archipelago"),
      React.createElement("div", { key: "rows" }, rows)
    ];

    if (canBuildButtons) {
      children.push(React.createElement("div", { key: "form", className: "ap-section__form" },
        FIELDS.map(function (f) {
          return React.createElement(EventButton,
            { key: f.id, event: fieldEvent(f.id) },
            label(f.label + "   " + shown(f)));
        })));

      children.push(React.createElement("div", { key: "actions", className: "ap-section__actions" },
        ACTIONS.map(function (a) {
          return React.createElement(EventButton,
            { key: a.id, event: actionEvent(a.id) }, label(actionLabel(a)));
        })));
    }

    children.push(React.createElement("p", { key: "hint", className: "ap-section__hint" },
      editing
        ? "Typing into " + editing.toUpperCase() + " -- select again to stop."
        : "Select a field to type into it."));

    return children;
  }

  var pageLink = null;

  function pageIsActive() {
    return !!(pageLink && pageLink.active);
  }

  function closePage() {
    if (pageIsActive()) {
      try { pageLink.deactivate(); } catch (e) { /* already gone */ }
    }
    leavePage();
  }

  function apSection() {
    return React.createElement(Section, {
      key: "ap-section",
      setLink: function (link) { pageLink = link; },
      routeNameLocID: AP_ROUTE,
      routeDisplayNameLocID: "Archipelago",
      hasTransition: true,
      onDeactivate: leavePage,
      getClasses: function (link, active) {
        return "ap-section" + (active ? "" : " ap-section--hidden");
      }
    }, apPage());
  }

  function apSectionPortal() {
    var rootLink = findRootLink();
    if (!canReparent || !rootLink) return null;
    return ReactDOM.createPortal(
      React.createElement(LinkContext.Provider, { value: rootLink }, apSection()),
      document.body,
      "ap-portal");
  }

  function apButton() {
    return React.createElement(SectionButton, {
      key: "ap-entry",
      section: AP_ROUTE,
      titleLocID: "Archipelago",
      hasTransition: true,
      onSetCb: enterPage
    });
  }

  function unwindCreateElement() {
    var guard = 0;
    while (React && React.createElement && React.createElement.__apPatched && guard++ < 100) {
      React.createElement = React.createElement.__apOriginal;
    }
  }

  function offLeakedHandlers() {
    if (!coherent || !coherent.off) return;
    GLOBAL.handlers.forEach(function (h) {
      try { coherent.off(h.event, h.fn); } catch (e) { /* already gone */ }
    });
    GLOBAL.handlers = [];
  }

  function listen(event, fn) {
    coherent.on(event, fn);
    GLOBAL.handlers.push({ event: event, fn: fn });
  }

  function isMenuRoot(config) {
    if (config.routeNameLocID !== "ROOT") return false;
    return config.key === "main-menu" || config.quitOnBack === "OnResumeClicked";
  }

  function carriesChildren(args) {
    return args.length > 2;
  }

  function installMenuEntry() {
    if (!canBuildMenu) return;

    unwindCreateElement();
    offLeakedHandlers();

    originalCreateElement = React.createElement;
    patchedCreateElement = function (type, config) {
      if (config && isMenuRoot(config) && carriesChildren(arguments)) {
        var args = Array.prototype.slice.call(arguments);
        args.splice(Math.max(2, args.length - 1), 0, apButton());
        var portal = apSectionPortal();
        if (portal) args.push(portal);
        return originalCreateElement.apply(this, args);
      }
      return originalCreateElement.apply(this, arguments);
    };
    patchedCreateElement.__apPatched = true;
    patchedCreateElement.__apOriginal = originalCreateElement;
    React.createElement = patchedCreateElement;

    if (canBuildButtons) {
      FIELDS.forEach(function (f) {
        f.handler = function () {
          setEditing(editing === f.id ? null : f.id);
          refreshMainMenu();
        };
        listen(fieldEvent(f.id), f.handler);
      });

      ACTIONS.forEach(function (a) {
        a.handler = function () {
          setEditing(null);
          if (a.id === "clear") {
            var target = fieldById(lastField);
            if (target) target.value = "";
          } else if (a.id === "connect") {
            var values = {};
            FIELDS.forEach(function (f) { values[f.id] = f.value; });
            send("action:connect " + JSON.stringify(values));
            state.status = "connecting...";
          } else {
            send("action:" + a.id);
          }
          refreshMainMenu();
        };
        listen(actionEvent(a.id), a.handler);
      });

      listen("OnCancel", function () {
        if (editing) { setEditing(null); refreshMainMenu(); }
      });

      listen("OnResumeClicked", leavePage);
      listen("ShowMenu", closePage);

      materializeHandlers(NAV_EVENTS);
      materializeHandlers(ACTIVATE_EVENTS);
    }
  }

  function removeMenuEntry() {
    resumeEvents(NAV_EVENTS);
    resumeEvents(ACTIVATE_EVENTS);
    if (document.body) document.body.classList.remove("ap-page-active");
    onPage = false;
    unwindCreateElement();
    originalCreateElement = patchedCreateElement = null;
    offLeakedHandlers();
    FIELDS.forEach(function (f) { f.handler = null; });
    ACTIONS.forEach(function (a) { a.handler = null; });
  }

  function instances() {
    var container = document.querySelector(".app-content");
    var rootContainer = container && container._reactRootContainer;
    var root = rootContainer && (rootContainer._internalRoot || rootContainer);
    var fiber = root && root.current;
    if (!fiber) return [];

    var out = [];
    var stack = [fiber];
    var guard = 0;
    while (stack.length && guard++ < 20000) {
      var node = stack.pop();
      if (node.stateNode) out.push(node.stateNode);
      if (node.child) stack.push(node.child);
      if (node.sibling) stack.push(node.sibling);
    }
    return out;
  }

  function findRootLink() {
    var found = null;
    instances().forEach(function (inst) {
      if (!found && inst && inst.rootLink) found = inst.rootLink;
    });
    return found;
  }

  function refreshMainMenu() {
    instances().forEach(function (inst) {
      if (inst && typeof inst.renderMainMenu === "function" && typeof inst.forceUpdate === "function") {
        inst.forceUpdate();
      }
    });
  }

  installMenuEntry();
  refreshMainMenu();

  // --- typing --------------------------------------------------------------------------------

  var shiftDown = false;
  var pending = null;

  // Flip to true to have every key event seen while editing reported to the client's console as
  // `[ui-js] key ...`. The view has no developer tools, so this is the only way to find out what a
  // platform actually delivers — which is how the macOS delete key above was pinned down. Capped
  // so a long session cannot flood the socket.
  var DEBUG_KEYS = false;
  var debugBudget = 60;

  function reportKey(e) {
    if (!DEBUG_KEYS || !editing || debugBudget <= 0) return;
    debugBudget--;
    send("error:key " + e.type
      + " keyCode=" + e.keyCode + " which=" + e.which + " charCode=" + e.charCode
      + " key=" + JSON.stringify(e.key) + " code=" + JSON.stringify(e.code)
      + " shift=" + e.shiftKey);
  }

  function insert(f, ch) {
    if (f.digits && (ch < "0" || ch > "9")) return false;
    if (f.value.length >= MAX_FIELD) return false;
    f.value += ch;
    refreshMainMenu();
    return true;
  }

  function erase(f) {
    if (!f.value.length) return false;
    f.value = f.value.slice(0, -1);
    refreshMainMenu();
    return true;
  }

  // What the delete key looks like here. The view reports Windows-style virtual key codes and
  // fills in neither `key` nor `code`, so the code number is all there is to go on — and on macOS
  // it maps the delete key to VK_DELETE (46) rather than VK_BACK (8), with no keypress following,
  // unlike a letter. Accept both codes: these fields have no cursor, so a forward delete has
  // nothing else it could mean. No printable key reports either code (`.` is 190 / 110).
  function isErase(e) {
    var kc = e.keyCode || e.which || 0;
    return kc === 8 || kc === 46;
  }

  function isShift(e) {
    return (e.keyCode || e.which || 0) === 16;
  }

  function fallbackChar(e) {
    var kc = e.keyCode;
    var shifted = shiftDown || e.shiftKey;

    if (kc >= 65 && kc <= 90) {
      var letter = String.fromCharCode(kc);
      return shifted ? letter : letter.toLowerCase();
    }
    if (shifted) return null;
    if (kc >= 48 && kc <= 57) return String.fromCharCode(kc);
    if (kc >= 96 && kc <= 105) return String.fromCharCode(kc - 48);
    if (kc === 190 || kc === 110) return ".";
    if (kc === 189 || kc === 109) return "-";
    return null;
  }

  // keydown and keypress describe the same keystroke, and only keypress knows which character the
  // layout and modifiers actually produced — but not every key gets one (the delete key does not).
  // So keydown never acts directly, it *schedules* its own coarser reading for the next turn of the
  // event loop, and a keypress, dispatched first, cancels it. Keys that produce a keypress are
  // handled by the handler that knows best; keys that do not still act, one turn later; and no
  // keystroke is ever applied twice.
  function schedule(apply) {
    var token = { used: false };
    pending = token;
    setTimeout(function () {
      if (token.used) return;
      if (pending === token) pending = null;
      apply();
    }, 0);
  }

  function consumePending() {
    if (!pending) return;
    pending.used = true;
    pending = null;
  }

  function onKeyDown(e) {
    if (isShift(e)) shiftDown = true;
    reportKey(e);
    if (!editing) return;
    var f = fieldById(editing);
    if (!f) return;

    if (isErase(e)) {
      schedule(function () { erase(f); });
      return;
    }

    var ch = fallbackChar(e);
    if (ch) schedule(function () { insert(f, ch); });
  }

  function onKeyUp(e) {
    if (isShift(e)) shiftDown = false;
    reportKey(e);
  }

  function onKeyPress(e) {
    consumePending();
    reportKey(e);

    if (!editing) return;
    var f = fieldById(editing);
    if (!f) return;

    var code = e.charCode || e.which || 0;
    if (code === 127 || isErase(e)) { erase(f); return; }
    if (code < 32) return;
    var ch = String.fromCharCode(code);

    if (shiftDown || e.shiftKey) {
      var upper = ch.toUpperCase();
      if (upper !== ch && ch >= "a" && ch <= "z") ch = upper;
    }

    insert(f, ch);
  }

  document.addEventListener("keydown", onKeyDown, true);
  document.addEventListener("keypress", onKeyPress, true);
  document.addEventListener("keyup", onKeyUp, true);

  // --- public API ----------------------------------------------------------------------------

  window.AP = {
    update: function (next) {
      for (var k in next) {
        if (Object.prototype.hasOwnProperty.call(next, k)) state[k] = next[k];
      }
      adoptSession(next.session);
      refreshMainMenu();
    },

    /*
     * Which elevator destinations Archipelago allows, as a map of sector id -> boolean
     * (0 EXECUTIVE, 1 RESEARCH, 2 MAINTENANCE, 3 PUMP_ROOM, 4 CONTAINMENT, 5 INVESTIGATION).
     */
    elevator: function (sectors) {
      window.APEV = sectors || undefined;
    },

    destroy: function () {
      document.removeEventListener("keydown", onKeyDown, true);
      document.removeEventListener("keypress", onKeyPress, true);
      document.removeEventListener("keyup", onKeyUp, true);
      removeMenuEntry();
      refreshMainMenu();
    }
  };
})();
