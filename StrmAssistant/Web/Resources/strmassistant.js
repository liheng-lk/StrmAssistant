define(['connectionManager', 'globalize', 'loading', 'toast', 'confirm', 'dialog'], function (connectionManager, globalize, loading, toast, confirm, dialog) {

    function getDeepDeleteLabel() {
        const locale = globalize.getCurrentLocale().toLowerCase();
        return locale === 'zh-cn' ? '深度删除' :
            (['zh-hk', 'zh-tw'].includes(locale) ? '深度刪除' : 'Deep Delete');
    }

    function formatDeepDeletePlan(plan) {
        const lines = [];
        if (plan.ItemName) lines.push(plan.ItemName);
        if (plan.SourcePath) lines.push(plan.SourcePath);
        lines.push('');

        const entries = plan.Entries || [];
        if (entries.length) {
            lines.push('Targets:');
            entries.slice(0, 30).forEach(entry => {
                lines.push((entry.Allowed ? '✓ ' : '✗ ') + entry.Path);
            });
            if (entries.length > 30) lines.push(`... +${entries.length - 30}`);
        }

        const warnings = plan.Warnings || [];
        if (warnings.length) {
            lines.push('');
            lines.push('Warnings:');
            warnings.forEach(value => lines.push('- ' + value));
        }

        if (plan.DryRun) {
            lines.push('');
            lines.push('Dry Run is enabled. Nothing will actually be deleted.');
        }

        return lines.join('\n');
    }

    return {
        copy: function (libraryId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let copyApi = apiClient.getUrl('Library/VirtualFolders/Copy');

            apiClient.ajax({
                type: "POST",
                url: copyApi,
                data: JSON.stringify({ Id: libraryId }),
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const locale = globalize.getCurrentLocale().toLowerCase();
                const confirmMessage = (locale === 'zh-cn') ? '\u590d\u5236\u5a92\u4f53\u5e93\u6210\u529f' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u88fd\u5a92\u9ad4\u5eab\u6210\u529f' : 'Copy Library Success');
                toast(confirmMessage);
                const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                if (itemsContainer) {
                    itemsContainer.notifyRefreshNeeded(true);
                }
            });
        },

        remove: function (libraryId, libraryName) {
            confirm({
                text: globalize.translate('MessageAreYouSureYouWishToRemoveLibrary').replace('{0}', libraryName),
                title: globalize.translate('HeaderRemoveLibrary'),
                confirmText: globalize.translate('Remove'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();

                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl('Library/VirtualFolders/Delete');

                apiClient.ajax({
                    type: "POST",
                    url: deleteApi + "?refreshLibrary=false&id=" + libraryId,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5408\u96c6\u5220\u9664\u6210\u529f' :
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u5408\u96C6\u5236\u9662\u6210\u529F' : 'Delete Collections Success');
                    toast(confirmMessage);
                    const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                    if (itemsContainer) {
                        itemsContainer.notifyRefreshNeeded(true);
                    }
                });
            });
        },

        deepdelete: function (itemId, itemName) {
            let apiClient = connectionManager.currentApiClient();
            const label = getDeepDeleteLabel();
            const planApi = apiClient.getUrl(`StrmAssistant/DeepDelete/${itemId}/Plan`);

            loading.show();
            return apiClient.ajax({
                type: 'GET',
                url: planApi,
                dataType: 'json'
            }).then(plan => {
                loading.hide();

                if (!plan || (plan.Errors && plan.Errors.length)) {
                    const error = plan?.Errors?.join('\n') || 'Unable to build deep-delete plan.';
                    toast(error);
                    return;
                }

                const blocked = (plan.Entries || []).some(entry => !entry.Allowed);
                if (blocked) {
                    toast((plan.Errors || []).concat(plan.Warnings || []).join('\n') ||
                        'Deep delete contains blocked paths. Check allowed roots.');
                    return;
                }

                return confirm({
                    text: formatDeepDeletePlan(plan),
                    title: label + ' - ' + (itemName || ''),
                    confirmText: plan.DryRun ? 'Dry Run' : globalize.translate('Delete'),
                    primary: 'cancel'
                }).then(function () {
                    loading.show();
                    const executeApi = apiClient.getUrl(`StrmAssistant/DeepDelete/${itemId}`);
                    return apiClient.ajax({
                        type: 'DELETE',
                        url: executeApi + '?Confirm=true',
                        data: {},
                        dataType: 'json',
                        contentType: 'application/json'
                    }).then(result => {
                        loading.hide();
                        if (!result) {
                            toast(label + ' failed');
                            return;
                        }

                        if (result.Errors && result.Errors.length) {
                            toast(result.Errors.join('\n'));
                            return;
                        }

                        if (result.DryRun && !result.Executed) {
                            toast(label + ': Dry Run');
                            return;
                        }

                        if (result.Success && result.Executed) {
                            toast(label + ' Success');
                            setTimeout(() => window.history.back(), 800);
                        } else {
                            toast((result.Warnings || []).join('\n') || label + ' not executed');
                        }
                    }).catch(error => {
                        loading.hide();
                        toast(error?.message || label + ' failed');
                    });
                });
            }).catch(error => {
                loading.hide();
                toast(error?.message || label + ' plan failed');
            });
        },

        traverse: function (itemId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let scanApi = apiClient.getUrl(`Items/${itemId}/Refresh`);
            let queryParams = {
                Recursive: true,
                ImageRefreshMode: 'Default',
                MetadataRefreshMode: 'Default',
                ReplaceAllImages: false,
                ReplaceAllMetadata: false
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${scanApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const confirmMessage = globalize.translate('ScanningLibraryFilesDots');
                toast(confirmMessage);
            });
        },

        delver: function (itemId, itemName, itemType) {
            if (itemType === 'Movie') {
                confirm({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    confirmText: globalize.translate('Delete'),
                    primary: 'cancel',
                    centerText: !1
                })
                .then(function() {
                    deleteVersion(itemId);
                });
            } else {
                const locale = globalize.getCurrentLocale().toLowerCase();
                const deleteEpisode = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Episode')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Episode');
                const deleteSeason = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Season')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Season');
                dialog({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    buttons: [
                        { name: globalize.translate("Cancel"), id: "cancel", type: "submit" },
                        { name: deleteEpisode, id: "deleteepisode", type: "cancel" },
                        { name: deleteSeason, id: "deleteseason", type: "cancel" }
                    ],
                    centerText: !1
                })
                .then(function(id) {
                    if (id === 'deleteepisode') {
                        deleteVersion(itemId);
                    } else if (id === 'deleteseason') {
                        deleteVersion(itemId, true);
                    }
                });
            }
            function deleteVersion(itemId, deleteParent = false) {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl(`Items/${itemId}/DeleteVersion${deleteParent ? `?DeleteParent=true` : ''}`);
                apiClient.ajax({
                    type: "POST",
                    url: deleteApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5220\u9664\u7248\u672C\u6210\u529F' :
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u524A\u9664\u7248\u672C\u6210\u529F' : 'Delete Version Success');
                    toast(confirmMessage);
                });
            }
        },

        lock: function (itemId, lockData) {
            let apiClient = connectionManager.currentApiClient();
            let lockApi = apiClient.getUrl(`Items/${itemId}/Lock`);
            let queryParams = {
                LockData: lockData
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${lockApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            });
        },

        clear_intro: function (itemId) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
            confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: globalize.translate('Clear'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let clearIntroApi = apiClient.getUrl(`Items/${itemId}/ClearIntro`);
                apiClient.ajax({
                    type: "POST",
                    url: clearIntroApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const confirmMessage = (locale === 'zh-cn') ? commandName + '\u6210\u529F' :
                        (['zh-hk', 'zh-tw'].includes(locale) ? commandName + '\u6210\u529F' : commandName + ' Success');
                    toast(confirmMessage);
                });
            });
        }
    };
});
