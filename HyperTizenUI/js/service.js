// Runs inside TizenBrew's Node.js sandbox (v12 on the QE55Q80A). Kept to plain ES5 syntax.
var http = require('http');
var net = require('net');

var SERVICE_APP_ID = 'io.gh.reisxd.HyperTizen';
var STATUS_PORT = 8087;
var SERVICE_CHECK_URL = { host: '127.0.0.1', port: 8086, path: '/logs', timeout: 1500 };
var SDB_HOST = '127.0.0.1';
var SDB_PORT = 26101;
var SDB_TIMEOUT_MS = 8000;
var SDB_LOCAL_ID = 12345;
var CMD = { CNXN: 0x4e584e43, OPEN: 0x4e45504f, OKAY: 0x59414b4f, WRTE: 0x45545257, CLSE: 0x45534c43, AUTH: 0x48545541 };
var RETRY_MS = 5000;
var MAX_ATTEMPTS = 24;

var status = {
    node: typeof process !== 'undefined' ? process.version : 'inconnu',
    tizenApi: typeof tizen !== 'undefined' && tizen && tizen.application ? 'oui' : 'non',
    serviceUp: false,
    attempts: 0,
    launching: false,
    log: []
};

function note(message) {
    status.log.push(new Date().toISOString() + ' ' + message);
    while (status.log.length > 40) status.log.shift();
    try { console.log('[HyperTizen service] ' + message); } catch (e) { }
}

function checkServiceUp(callback) {
    var finished = false;
    function done(up) {
        if (finished) return;
        finished = true;
        callback(up);
    }
    try {
        var req = http.get(SERVICE_CHECK_URL, function (res) {
            res.resume();
            done(res.statusCode === 200);
        });
        req.on('error', function () { done(false); });
        req.setTimeout(SERVICE_CHECK_URL.timeout, function () {
            try { req.abort(); } catch (e) { }
            done(false);
        });
    } catch (e) {
        done(false);
    }
}

function sdbChecksum(data) {
    var sum = 0;
    for (var i = 0; i < data.length; i++) sum = (sum + data[i]) >>> 0;
    return sum;
}

// Same 24-byte header as the ADB protocol that adbhost (used by TizenBrew) speaks to the TV's sdbd.
function sdbPacket(command, arg0, arg1, text) {
    var data = Buffer.alloc(0);
    if (text) data = Buffer.concat([Buffer.from(text, 'utf8'), Buffer.alloc(1)]);
    var header = Buffer.alloc(24);
    header.writeUInt32LE(command >>> 0, 0);
    header.writeUInt32LE(arg0 >>> 0, 4);
    header.writeUInt32LE(arg1 >>> 0, 8);
    header.writeUInt32LE(data.length, 12);
    header.writeUInt32LE(sdbChecksum(data), 16);
    header.writeUInt32LE((0xFFFFFFFF - command) >>> 0, 20);
    return Buffer.concat([header, data]);
}

// Launches the native service the way `tizen run` does: shell "0 debug <appid>" over sdb on 127.0.0.1.
function launchViaSdb(done) {
    var output = '';
    var pending = Buffer.alloc(0);
    var finished = false;
    var socket;

    function finish(why) {
        if (finished) return;
        finished = true;
        try { socket.destroy(); } catch (e) { }
        var ok = /launched|success/i.test(output);
        note('sdb: ' + why + (output ? ' | sortie: ' + output.replace(/\s+/g, ' ').trim() : '') + ' | ' + (ok ? 'lancement OK' : 'pas de confirmation'));
        done(ok);
    }

    try {
        socket = net.connect(SDB_PORT, SDB_HOST);
    } catch (e) {
        note('sdb: connexion impossible: ' + e.message);
        done(false);
        return;
    }
    socket.setTimeout(SDB_TIMEOUT_MS, function () { finish('delai depasse'); });
    socket.on('error', function (e) { finish('erreur: ' + e.message); });
    socket.on('close', function () { finish('connexion fermee'); });
    socket.on('connect', function () { socket.write(sdbPacket(CMD.CNXN, 0x01000000, 4096, 'host::')); });
    socket.on('data', function (chunk) {
        pending = Buffer.concat([pending, chunk]);
        while (pending.length >= 24) {
            var length = pending.readUInt32LE(12);
            if (pending.length < 24 + length) break;
            var command = pending.readUInt32LE(0);
            var arg0 = pending.readUInt32LE(4);
            var data = pending.slice(24, 24 + length);
            pending = pending.slice(24 + length);

            if (command === CMD.CNXN) {
                note('sdb: connecte a ' + data.toString().replace(/\0/g, ''));
                socket.write(sdbPacket(CMD.OPEN, SDB_LOCAL_ID, 0, 'shell:0 debug ' + SERVICE_APP_ID));
            } else if (command === CMD.AUTH) {
                finish('authentification demandee par la TV');
                return;
            } else if (command === CMD.WRTE) {
                output += data.toString();
                socket.write(sdbPacket(CMD.OKAY, SDB_LOCAL_ID, arg0));
            } else if (command === CMD.CLSE) {
                finish('commande terminee');
                return;
            }
        }
    });
}

function launchViaAppControl(onFailure) {
    try {
        tizen.application.launchAppControl(
            new tizen.ApplicationControl('http://tizen.org/appcontrol/operation/service'),
            SERVICE_APP_ID,
            function () { note('launchAppControl(service) accepte'); },
            function (e) {
                note('launchAppControl(service) refuse: ' + (e && e.message ? e.message : e));
                onFailure();
            }
        );
    } catch (e) {
        note('launchAppControl(service) exception: ' + e.message);
        onFailure();
    }
}

function launchViaLaunch() {
    try {
        tizen.application.launch(
            SERVICE_APP_ID,
            function () { note('launch() accepte'); },
            function (e) { note('launch() refuse: ' + (e && e.message ? e.message : e)); }
        );
    } catch (e) {
        note('launch() exception: ' + e.message);
    }
}

function tryLaunch(reason) {
    if (status.launching) return;
    status.launching = true;
    checkServiceUp(function (up) {
        status.serviceUp = up;
        if (up) {
            note('service deja actif (' + reason + ')');
            status.launching = false;
            return;
        }
        status.attempts++;
        note('lancement du service (' + reason + ', essai ' + status.attempts + ')');
        launchViaSdb(function (ok) {
            if (ok) {
                status.launching = false;
                return;
            }
            if (typeof tizen === 'undefined' || !tizen || !tizen.application) {
                note('tizen.application indisponible dans ce bac a sable');
                status.launching = false;
                return;
            }
            launchViaAppControl(launchViaLaunch);
            setTimeout(function () { status.launching = false; }, 2000);
        });
    });
}

function send(res, code, body) {
    res.writeHead(code, {
        'Content-Type': 'application/json; charset=utf-8',
        'Access-Control-Allow-Origin': '*'
    });
    res.end(JSON.stringify(body));
}

try {
    var server = http.createServer(function (req, res) {
        if (req.url === '/launch') {
            tryLaunch('demande manuelle');
            setTimeout(function () { send(res, 200, status); }, 2500);
        } else {
            send(res, 200, status);
        }
    });
    server.on('error', function (e) { note('serveur etat indisponible: ' + e.message); });
    server.listen(STATUS_PORT, function () { note('etat sur le port ' + STATUS_PORT + ' (/status, /launch)'); });
} catch (e) {
    note('serveur etat impossible: ' + e.message);
}

note('demarrage: node ' + status.node + ', tizen.application ' + status.tizenApi);
tryLaunch('demarrage');

var retries = 0;
var retryTimer = setInterval(function () {
    retries++;
    if (status.serviceUp || retries >= MAX_ATTEMPTS) {
        clearInterval(retryTimer);
        return;
    }
    tryLaunch('nouvel essai');
}, RETRY_MS);
