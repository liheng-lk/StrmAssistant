define([], function () {
    'use strict';

    // 2.0.9+: settings are rendered by Emby's native IHasTabbedUIPages implementation.
    // This module remains only as a compatibility no-op for browsers that cached an older
    // StrmAssistant shortcuts.js loader. It must never mutate GenericUI DOM again.
    function retire() {
        try {
            window.__strmAssistantSettingsTabsLoaded = false;
            window.__strmAssistantSettingsTabsRetired = true;
            window.__strmAssistantSettingsTabsModule = 'native-emby-plugin-ui';
        } catch (_) {}
        return false;
    }

    return { init: retire, refresh: retire };
});
