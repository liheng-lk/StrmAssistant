using MediaBrowser.Controller.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Web.Helper
{
    internal static class ShortcutMenuHelper
    {
        public static string ModifiedShortcutsString { get; private set; }

        public static MemoryStream StrmAssistantJs { get; private set; }

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                StrmAssistantJs = GetResourceStream("strmassistant.js");
                ModifyShortcutMenu(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            var manifestResourceStream = typeof(ShortcutMenuHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int) manifestResourceStream.Length);
            manifestResourceStream.CopyTo(destination);
            return destination;
        }

        private static void ModifyShortcutMenu(IServerConfigurationManager configurationManager)
        {
            string shortcutsJs;
            var shortcutsJsStream = GetResourceStream("shortcuts.js");
            shortcutsJsStream.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(shortcutsJsStream))
            {
                shortcutsJs = reader.ReadToEnd();
            }

            const string injectShortcutCommand = @"
const strmAssistantCommandSource = {
    getCommands: function(options) {
        const locale = this.globalize.getCurrentLocale().toLowerCase();
        const cjk = ['zh', 'ja', 'ko'].some(lang => locale.startsWith(lang));
        const lockCommandName = ({
            'zh-cn': '\u9501\u5B9A',
            'zh-hk': '\u9396\u5B9A',
            'zh-tw': '\u9396\u5B9A'
        }[locale] || 'Lock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const unlockCommandName = ({
            'zh-cn': '\u89E3\u9501',
            'zh-hk': '\u89E3\u9396',
            'zh-tw': '\u89E3\u9396'
        }[locale] || 'Unlock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const deepDeleteCommandName = ({
            'zh-cn': '\u6DF1\u5EA6\u5220\u9664',
            'zh-hk': '\u6DF1\u5EA6\u522A\u9664',
            'zh-tw': '\u6DF1\u5EA6\u522A\u9664'
        }[locale] || 'Deep Delete');
        const clearThumbnailCommandName = ({
            'zh-cn': '\u6E05\u9664\u7AE0\u8282\u56FE/BIF\u7F13\u5B58',
            'zh-hk': '\u6E05\u9664\u7AE0\u7BC0\u5716/BIF\u5FEB\u53D6',
            'zh-tw': '\u6E05\u9664\u7AE0\u7BC0\u5716/BIF\u5FEB\u53D6'
        }[locale] || 'Clear Chapter/BIF Cache');
        const clearMediaInfoCommandName = ({
            'zh-cn': '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F',
            'zh-hk': '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A',
            'zh-tw': '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A'
        }[locale] || 'Clear MediaInfo');
        const personDuplicateCommandName = ({
            'zh-cn': '\u68C0\u67E5/\u6E05\u7406\u91CD\u590D\u4EBA\u7269',
            'zh-hk': '\u6AA2\u67E5/\u6E05\u7406\u91CD\u8907\u4EBA\u7269',
            'zh-tw': '\u6AA2\u67E5/\u6E05\u7406\u91CD\u8907\u4EBA\u7269'
        }[locale] || 'Check/Clear Duplicate Person');

        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType !== 'boxsets' && options.items[0].CollectionType !== 'playlists') {
            const commandName = (locale === 'zh-cn') ? '\u590D\u5236' : (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u8F38' : 'Copy');
            return [{ name: commandName, id: 'copy', icon: 'content_copy' }];
        }
        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType === 'boxsets') {
            return [{ name: this.globalize.translate('Remove'), id: 'remove', icon: 'remove_circle_outline' }];
        }
        if (options.items?.length === 1) {
            const result = [];
            const item = options.items[0];
            const isAdmin = (options.user && options.user.Policy.IsAdministrator) || false;

            if (item.Type === 'Movie') {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if ((item.Type === 'Movie' || item.Type === 'Episode') &&
                 item.CanDelete && options.mediaSourceId && item.MediaSources.length > 1) {
                result.push({
                    name: cjk
                        ? this.globalize.translate('Delete') + this.globalize.translate('Version')
                        : this.globalize.translate('Delete') + ' ' + this.globalize.translate('Version'),
                    id: 'delver_' + options.mediaSourceId,
                    icon: 'remove'
                });
            }
            if (item.CanDelete && isAdmin &&
                ['Movie', 'Episode', 'Video', 'MusicVideo', 'Audio'].includes(item.Type)) {
                result.push({ name: deepDeleteCommandName, id: 'deep_delete', icon: 'delete_forever' });
            }
            if (isAdmin && ['Movie', 'Episode', 'Video', 'MusicVideo'].includes(item.Type)) {
                result.push({ name: clearThumbnailCommandName, id: 'clear_thumbnails', icon: 'image_not_supported' });
            }
            if (isAdmin && ['Movie', 'Episode', 'Video', 'MusicVideo', 'Audio'].includes(item.Type)) {
                result.push({ name: clearMediaInfoCommandName, id: 'clear_mediainfo', icon: 'restart_alt' });
            }
            if (isAdmin && item.Type === 'Person') {
                result.push({ name: personDuplicateCommandName, id: 'person_duplicates', icon: 'person_remove' });
            }
            if (item.hasOwnProperty('LockData') && item.Type !== 'CollectionFolder' && isAdmin) {
                if (item.LockData) {
                    result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
                } else {
                    result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                }
            }
            if ((item.Type === 'Series' || item.Type === 'Season') && isAdmin) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 && options.items[0].Type !== 'CollectionFolder' &&
            ((options.users && Object.values(options.users)[0]?.Policy.IsAdministrator) || false)) {
            const result = [];
            result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
            result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            return result;
        }
        return [];
    },
    executeCommand: function(command, items) {
        if (!command || !items?.length) return;
        const actions = {
            copy: 'copy',
            remove: 'remove',
            traverse: 'traverse',
            deep_delete: 'deepdelete',
            clear_thumbnails: 'clear_thumbnails',
            clear_mediainfo: 'clear_mediainfo',
            lock: 'lock',
            unlock: 'unlock',
            clear_intro: 'clear_intro'
        };
        if (command === 'person_duplicates') {
            return require(['connectionManager', 'loading', 'toast', 'confirm']).then(modules => {
                const connectionManager = modules[0];
                const loading = modules[1];
                const toast = modules[2];
                const confirm = modules[3];
                const apiClient = connectionManager.currentApiClient();
                const selected = items[0];
                const locale = strmAssistantCommandSource.globalize.getCurrentLocale().toLowerCase();
                const title = locale === 'zh-cn' ? '\u6E05\u7406\u91CD\u590D\u4EBA\u7269' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u7406\u91CD\u8907\u4EBA\u7269' : 'Clear Duplicate Person');
                const planUrl = apiClient.getUrl(`StrmAssistant/People/${selected.Id}/Duplicates/Plan`);
                loading.show();
                return apiClient.ajax({ type: 'GET', url: planUrl, dataType: 'json' }).then(plan => {
                    loading.hide();
                    if (!plan || plan.Success === false) {
                        toast(plan?.Error || 'Duplicate-person plan failed');
                        return;
                    }
                    const lines = [plan.SelectedName || selected.Name || '', ''];
                    (plan.MatchedProviderIds || []).forEach(value => lines.push(value));
                    const candidates = plan.Candidates || [];
                    if (candidates.length) {
                        lines.push('');
                        lines.push('Candidates:');
                        candidates.forEach(candidate => {
                            const prefix = candidate.Selected ? '\u2713 KEEP ' : (candidate.PlannedForDeletion ? '\u2717 DELETE ' : '- ');
                            lines.push(`${prefix}${candidate.Name || ''} [${candidate.Id}] - related: ${candidate.RelatedItemCount || 0}`);
                        });
                    }
                    (plan.Warnings || []).forEach(value => lines.push('! ' + value));
                    if (!(plan.DeleteIds || []).length) {
                        toast((plan.Warnings || []).join('\n') || 'No duplicate person found');
                        return;
                    }
                    return confirm({
                        text: lines.join('\n'),
                        title: title,
                        confirmText: strmAssistantCommandSource.globalize.translate('Delete'),
                        primary: 'cancel'
                    }).then(() => {
                        loading.show();
                        const clearUrl = apiClient.getUrl(`StrmAssistant/People/${selected.Id}/Duplicates/Clear`) + '?Confirm=true';
                        return apiClient.ajax({
                            type: 'POST', url: clearUrl, data: {}, dataType: 'json', contentType: 'application/json'
                        }).then(result => {
                            loading.hide();
                            if (result?.Success && result?.Executed) {
                                toast(title + ' Success');
                            } else {
                                toast(result?.Error || title + ' not executed');
                            }
                        }).catch(error => {
                            loading.hide();
                            toast(error?.message || title + ' failed');
                        });
                    });
                }).catch(error => {
                    loading.hide();
                    toast(error?.message || 'Duplicate-person plan failed');
                });
            });
        }
        if (command.startsWith('delver_')) {
            const mediaSourceId = command.replace('delver_', '');
            const mediaSources = items[0].MediaSources || [];
            const matchingItem = mediaSources.find(source => source.Id === mediaSourceId);
            const itemId = matchingItem?.ItemId;
            const itemName = matchingItem?.Name;
            if (itemId && itemName) {
                return require(['components/strmassistant/strmassistant']).then(responses => {
                    return responses[0].delver(itemId, itemName, items[0].Type);
                });
            }
        }
        if (command === actions.lock || command === actions.unlock) {
            const lockData = command === actions.lock;
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const promises = items.map(item => responses[0].lock(item.Id, lockData));
                return Promise.all(promises);
            });
        }
        if (actions[command]) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                if (command === 'traverse') {
                    return responses[0][actions[command]](items[0].ParentId);
                }
                return responses[0][actions[command]](items[0].Id, items[0].Name);
            });
        }
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/globalize.js').then(globalize => {
        strmAssistantCommandSource.globalize = globalize;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js').then(itemmanager => {
            itemmanager.registerCommandSource(strmAssistantCommandSource);
        });
    });
}, 3000);
    ";

            var dataExplorer2Assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Emby.DataExplorer2");
            ModifiedShortcutsString = shortcutsJs + injectShortcutCommand;

            if (dataExplorer2Assembly != null)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"{nameof(ShortcutMenuHelper)} - Emby.DataExplorer2 plugin is installed");
                }

                var contextMenuHelperType = dataExplorer2Assembly.GetType("Emby.DataExplorer2.Api.ContextMenuHelper");
                var modifiedShortcutsProperty = contextMenuHelperType?.GetProperty("ModifiedShortcutsString",
                    BindingFlags.Static | BindingFlags.Public);
                var setMethod = modifiedShortcutsProperty?.GetSetMethod(true);

                if (setMethod != null)
                {
                    const string injectDataExplorerCommand = @"
const dataExplorerCommandSource = {
    getCommands(options) {
        const commands = [];
        if (options.items?.length === 1 && options.items[0].ProviderIds) {
            commands.push({
                name: 'Explore Item Data',
                id: 'dataexplorer',
                icon: 'manage_search'
            });
        }
        return commands;
    },
    executeCommand(command, items) {
        return require(['components/dataexplorer/dataexplorer']).then((responses) => {
            return responses[0].show(items[0].Id);
        });
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/itemmanager/itemmanager.js').then((itemmanager) => {
        itemmanager.registerCommandSource(dataExplorerCommandSource);
    });
}, 5000);
";
                    ModifiedShortcutsString += injectDataExplorerCommand;
                    setMethod.Invoke(null, new object[] { ModifiedShortcutsString });
                }
            }
        }
    }
}
