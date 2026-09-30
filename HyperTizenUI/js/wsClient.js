var SERVICE_APP_ID = 'io.gh.reisxd.HyperTizen';
var SERVICE_PORT = 8086;
var POLL_MS = 3000;
var RECONNECT_MS = 3000;
var DEFAULT_FBS_PORT = '19400';

var events = {
    SetConfig: 0,
    ReadConfig: 1,
    ReadConfigResult: 2,
    ScanSSDP: 3,
    SSDPScanResult: 4
};

var POLL_KEYS = ['enabled', 'capturing', 'cap_mode', 'connected', 'fbsServer', 'logs'];

var client = null;
var deviceIP = null;
var reconnectTimer = null;
var pollTimer = null;
var failedAttempts = 0;
var launchTried = false;
var launchedAt = 0;
var LAUNCH_GRACE_MS = 15000;
var fbsInputTouched = false;
var lastCloseCode = '-';
var connectAttempts = 0;

var state = {
    service: false,
    enabled: false,
    capturing: false,
    capMode: '-',
    connected: false,
    fbsServer: '',
    logs: ''
};

function byId(id) {
    return document.getElementById(id);
}

function showTarget() {
    var el = byId('target');
    if (!el) return;
    var api = (typeof tizen !== 'undefined' && tizen.application) ? 'oui' : 'non';
    el.textContent = 'Cible : ws://' + deviceIP + ':' + SERVICE_PORT + ' | tentative ' + connectAttempts +
        ' | dernier code ' + lastCloseCode + ' | API Tizen : ' + api;
}

function showMessage(text) {
    var el = byId('message');
    if (el) el.textContent = text || '';
}

function render() {
    var svc = byId('svcState');
    svc.textContent = state.service ? 'Connecte' : 'Injoignable';
    svc.className = 'value ' + (state.service ? 'ok' : 'bad');

    byId('capState').textContent = state.service ? (state.enabled ? 'Activee' : 'Desactivee') : '-';
    byId('capMode').textContent = state.service ? state.capMode : '-';

    var hh = byId('hhState');
    if (!state.service) {
        hh.textContent = '-';
        hh.className = 'value';
    } else if (state.connected) {
        hh.textContent = 'Connecte (' + state.fbsServer + ')';
        hh.className = 'value ok';
    } else {
        hh.textContent = state.enabled ? 'Non connecte' : 'En attente';
        hh.className = 'value ' + (state.enabled ? 'bad' : '');
    }

    byId('btnToggle').textContent = state.enabled ? 'Desactiver la capture' : 'Activer la capture';
    byId('btnToggle').disabled = !state.service;
    byId('btnSave').disabled = !state.service;

    var input = byId('fbsInput');
    if (!fbsInputTouched && state.fbsServer && document.activeElement !== input) {
        input.value = state.fbsServer;
    }

    byId('logs').textContent = state.logs;
}

function send(obj) {
    if (!client || client.readyState !== 1) return false;
    client.send(JSON.stringify(obj));
    return true;
}

function readKey(key) {
    send({ event: events.ReadConfig, key: key });
}

function setKey(key, value) {
    send({ event: events.SetConfig, key: key, value: value });
}

function poll() {
    for (var i = 0; i < POLL_KEYS.length; i++) readKey(POLL_KEYS[i]);
}

function onOpen() {
    failedAttempts = 0;
    state.service = true;
    showMessage('');
    poll();
    clearInterval(pollTimer);
    pollTimer = setInterval(poll, POLL_MS);
    render();
}

function onMessage(ev) {
    var msg;
    try {
        msg = JSON.parse(ev.data);
    } catch (e) {
        return;
    }
    if (msg.Event !== events.ReadConfigResult) return;
    applyResult(msg);
}

function applyResult(msg) {
    if (msg.error) {
        if (msg.key === 'fbsServer') state.fbsServer = '';
        if (msg.key === 'cap_mode') state.capMode = '-';
        render();
        return;
    }
    switch (msg.key) {
        case 'enabled': state.enabled = msg.value === 'true'; break;
        case 'capturing': state.capturing = msg.value === 'true'; break;
        case 'connected': state.connected = msg.value === 'true'; break;
        case 'cap_mode': state.capMode = msg.value === 'secvideo' ? 'Video complete (NV12)' : msg.value; break;
        case 'fbsServer': state.fbsServer = msg.value; break;
        case 'logs': state.logs = msg.value; break;
    }
    render();
}

function onClose(ev) {
    lastCloseCode = ev && ev.code !== undefined ? ev.code : '-';
    showTarget();
    var wasUp = state.service;
    state.service = false;
    clearInterval(pollTimer);
    render();
    failedAttempts++;

    if (wasUp) {
        showMessage('Connexion au service perdue. Nouvelle tentative...');
    } else if (failedAttempts >= 2 && !launchTried) {
        launchTried = true;
        launchService(true);
    } else if (failedAttempts >= 2 && new Date().getTime() - launchedAt > LAUNCH_GRACE_MS) {
        showMessage('Service injoignable. Appuyez sur "Demarrer le service" pour relancer.');
    }
    scheduleReconnect();
}

function scheduleReconnect() {
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connect, RECONNECT_MS);
}

function connect() {
    var ws;
    connectAttempts++;
    showTarget();
    try {
        ws = new WebSocket('ws://' + deviceIP + ':' + SERVICE_PORT);
    } catch (e) {
        lastCloseCode = 'exception ' + e.message;
        showTarget();
        scheduleReconnect();
        return;
    }
    client = ws;
    ws.onopen = onOpen;
    ws.onmessage = onMessage;
    ws.onclose = onClose;
    ws.onerror = function () { };
}

var HELPER_PORT = 8087;

// Asks the module's Node service (js/service.js, port 8087) to launch the native service.
function launchViaHelper(automatic) {
    showMessage(automatic ? 'Service injoignable : demande de lancement a TizenBrew...' : 'Demande de lancement a TizenBrew...');
    launchedAt = new Date().getTime();
    fetch('http://' + deviceIP + ':' + HELPER_PORT + '/launch')
        .then(function (res) { return res.json(); })
        .then(function (info) {
            var last = info.log && info.log.length ? info.log[info.log.length - 1] : '';
            showMessage((info.serviceUp ? 'Service actif. ' : 'Lancement demande. ') + 'API Tizen cote TizenBrew : ' + info.tizenApi + '. ' + last);
        })
        .catch(function () {
            showMessage('Script de service TizenBrew injoignable (port ' + HELPER_PORT + '). Autorisez-le dans TizenBrew (Autolaunch service) ou lancez le service depuis le PC.');
        });
}

function launchService(automatic) {
    if (typeof tizen === 'undefined' || !tizen.application) {
        launchViaHelper(automatic);
        return;
    }
    showMessage(automatic ? 'Service injoignable : tentative de lancement...' : 'Lancement du service...');
    launchedAt = new Date().getTime();
    try {
        tizen.application.launch(
            SERVICE_APP_ID,
            function () { showMessage('Lancement demande. Attente du service...'); },
            function (e) { showMessage('Lancement refuse : ' + (e && e.message ? e.message : e)); }
        );
    } catch (e) {
        showMessage('Lancement impossible : ' + e.message);
    }
}

function toggleCapture() {
    if (!state.service) return;
    var next = !state.enabled;
    setKey('enabled', next ? 'true' : 'false');
    state.enabled = next;
    showMessage(next ? 'Capture activee.' : 'Capture desactivee.');
    render();
    setTimeout(poll, 800);
}

function normalizeAddress(text) {
    var value = (text || '').replace(/^\s+|\s+$/g, '');
    if (!value) return null;
    if (value.indexOf(':') === -1) value += ':' + DEFAULT_FBS_PORT;
    if (!/^[A-Za-z0-9.\-]+:\d{2,5}$/.test(value)) return null;
    return parseInt(value.split(':')[1], 10) <= 65535 ? value : null;
}

function saveAddress() {
    if (!state.service) return;
    var address = normalizeAddress(byId('fbsInput').value);
    if (!address) {
        showMessage('Adresse invalide. Exemple : 192.168.1.38:19400');
        return;
    }
    setKey('fbsServer', address);
    state.fbsServer = address;
    fbsInputTouched = false;
    byId('fbsInput').value = address;
    showMessage('Adresse enregistree : ' + address);
    render();
    setTimeout(poll, 1500);
}

function resolveIP(callback) {
    var done = false;
    function finish(ip) {
        if (done) return;
        done = true;
        callback(ip);
    }
    setTimeout(function () { finish('127.0.0.1'); }, 2000);
    try {
        fetch('http://127.0.0.1:8081')
            .then(function (res) { return res.text(); })
            .then(function (text) {
                var ip = (text || '').replace(/^\s+|\s+$/g, '');
                finish(/^\d{1,3}(\.\d{1,3}){3}$/.test(ip) ? ip : '127.0.0.1');
            })
            .catch(function () { finish('127.0.0.1'); });
    } catch (e) {
        finish('127.0.0.1');
    }
}

function setupNavigation() {
    var ids = ['btnLaunch', 'btnToggle', 'fbsInput', 'btnSave'];

    function focusables() {
        var list = [];
        for (var i = 0; i < ids.length; i++) {
            var el = byId(ids[i]);
            if (el && !el.disabled) list.push(el);
        }
        return list;
    }

    document.addEventListener('keydown', function (e) {
        var list = focusables();
        var index = list.indexOf(document.activeElement);
        var isInput = document.activeElement && document.activeElement.tagName === 'INPUT';

        if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (list.length) list[Math.min(list.length - 1, index + 1)].focus();
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            if (list.length) list[Math.max(0, index - 1)].focus();
        } else if (e.key === 'Enter' && !isInput) {
            e.preventDefault();
            if (index >= 0) list[index].click();
        }
    });
}

window.onload = function () {
    byId('btnLaunch').onclick = function () { launchService(false); };
    byId('btnToggle').onclick = toggleCapture;
    byId('btnSave').onclick = saveAddress;
    byId('fbsInput').oninput = function () { fbsInputTouched = true; };

    setupNavigation();
    render();
    byId('btnLaunch').focus();

    resolveIP(function (ip) {
        deviceIP = ip;
        connect();
    });
};
