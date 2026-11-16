"use strict";
// The installed app's background service (config.xml, tizen:service). A TV refuses a web app's page that
// launches a service from another package, but allows it from the app's background service, which is how
// TizenBrew starts TizenGlow too (js/service.js). The page starts this (wsClient.js, launchService) and
// this starts the TV service. Plain ES5: older TVs run background services on Node.js 4.

var serviceAppId = 'io.gh.duckieonquacks.TizenGlow';

function launch() {
    try {
        tizen.application.launch(serviceAppId, function () { }, function () { });
    } catch (e) { }
}

module.exports.onStart = launch;
module.exports.onRequest = launch; // the page asks again while it waits for the TV service to answer
module.exports.onExit = function () { };
