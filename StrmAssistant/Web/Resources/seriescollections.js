define(['connectionManager', 'globalize'], function (connectionManager, globalize) {
    let installed = false;
    let renderToken = 0;
    let lastKey = '';

    function queryItemId() {
        const candidates = [];
        try { candidates.push(new URL(window.location.href).searchParams.get('id')); } catch (_) {}
        const hash = window.location.hash || '';
        const queryIndex = hash.indexOf('?');
        if (queryIndex >= 0) {
            try { candidates.push(new URLSearchParams(hash.substring(queryIndex + 1)).get('id')); } catch (_) {}
        }
        const match = hash.match(/(?:^|[?&])id=([0-9]+)/i);
        if (match) candidates.push(match[1]);
        return candidates.find(value => value && /^\d+$/.test(value)) || null;
    }

    function currentUserId(apiClient) {
        try {
            if (typeof apiClient.getCurrentUserId === 'function') return apiClient.getCurrentUserId();
        } catch (_) {}
        try {
            if (typeof apiClient.getCurrentUser === 'function') return apiClient.getCurrentUser()?.Id;
        } catch (_) {}
        return null;
    }

    function apiGet(apiClient, url) {
        return apiClient.ajax({ type: 'GET', url: url, dataType: 'json' });
    }

    function label(key) {
        const locale = (globalize.getCurrentLocale?.() || '').toLowerCase();
        if (key === 'title') {
            if (locale === 'zh-cn') return '所属合集';
            if (locale === 'zh-hk' || locale === 'zh-tw') return '所屬合集';
            return 'Collections';
        }
        if (key === 'direct') {
            if (locale === 'zh-cn') return '节目';
            if (locale === 'zh-hk' || locale === 'zh-tw') return '節目';
            return 'Series';
        }
        if (locale === 'zh-cn') return '季';
        if (locale === 'zh-hk' || locale === 'zh-tw') return '季';
        return 'Season';
    }

    function activeDetailContainer() {
        const selectors = [
            '.view-itemdetail:not(.hide) .detailPageContent',
            '.view-itemdetail:not(.hide) .detailPageContentContainer',
            '.itemDetailPage:not(.hide) .detailPageContent',
            '.itemDetailPage:not(.hide) .detailPageContentContainer',
            '.view-itemdetail:not(.hide)',
            '.itemDetailPage:not(.hide)'
        ];
        for (const selector of selectors) {
            const value = document.querySelector(selector);
            if (value) return value;
        }
        return null;
    }

    function clearExisting() {
        document.querySelectorAll('.strmassistant-series-collections').forEach(value => value.remove());
    }

    function collectionHref(id) {
        const hash = window.location.hash || '';
        const prefix = hash.startsWith('#!/') ? '#!/' : '#/';
        return prefix + 'item?id=' + encodeURIComponent(id);
    }

    function renderSection(result, itemId, token) {
        if (token !== renderToken || !result?.Success || !(result.Collections || []).length) {
            if (token === renderToken) clearExisting();
            return;
        }

        const host = activeDetailContainer();
        if (!host) {
            // The route can fire before the detail template is mounted. Retry a few bounded times.
            setTimeout(() => {
                if (token === renderToken) renderSection(result, itemId, token);
            }, 350);
            return;
        }

        clearExisting();
        const section = document.createElement('section');
        section.className = 'strmassistant-series-collections verticalSection';
        section.dataset.itemId = itemId;
        section.style.marginTop = '1.5em';

        const heading = document.createElement('h2');
        heading.className = 'sectionTitle sectionTitle-cards';
        heading.textContent = label('title');
        section.appendChild(heading);

        const row = document.createElement('div');
        row.className = 'strmassistant-series-collections-row';
        row.style.display = 'flex';
        row.style.flexWrap = 'wrap';
        row.style.gap = '.65em';

        (result.Collections || []).forEach(entry => {
            const link = document.createElement('a');
            link.className = 'raised button-submit emby-button strmassistant-series-collection-link';
            link.href = collectionHref(entry.Id);
            link.style.textDecoration = 'none';
            link.style.padding = '.55em .9em';
            link.style.maxWidth = '100%';

            const detail = [];
            if (entry.ContainsSeriesDirectly) detail.push(label('direct'));
            if ((entry.SeasonNames || []).length) detail.push(label('season') + ': ' + entry.SeasonNames.join(', '));
            link.textContent = entry.Name + (detail.length ? ' · ' + detail.join(' / ') : '');
            row.appendChild(link);
        });

        section.appendChild(row);
        host.appendChild(section);
    }

    async function render() {
        const token = ++renderToken;
        const itemId = queryItemId();
        if (!itemId) {
            lastKey = '';
            clearExisting();
            return;
        }

        const apiClient = connectionManager.currentApiClient();
        if (!apiClient) return;
        const userId = currentUserId(apiClient);
        if (!userId) return;

        const key = userId + ':' + itemId + ':' + window.location.hash;
        if (key === lastKey && document.querySelector('.strmassistant-series-collections')) return;

        try {
            const itemUrl = apiClient.getUrl('Users/' + encodeURIComponent(userId) + '/Items/' + encodeURIComponent(itemId));
            const item = await apiGet(apiClient, itemUrl);
            if (token !== renderToken || item?.Type !== 'Series') {
                if (token === renderToken) clearExisting();
                return;
            }

            const endpoint = apiClient.getUrl('StrmAssistant/SeriesCollections/' + encodeURIComponent(itemId)) +
                '?UserId=' + encodeURIComponent(userId);
            const result = await apiGet(apiClient, endpoint);
            if (token !== renderToken) return;
            lastKey = key;
            renderSection(result, itemId, token);
        } catch (_) {
            if (token === renderToken) clearExisting();
        }
    }

    function schedule() {
        setTimeout(render, 160);
    }

    function init() {
        if (installed) return;
        installed = true;
        window.addEventListener('hashchange', schedule, false);
        window.addEventListener('popstate', schedule, false);
        document.addEventListener('viewshow', schedule, false);
        document.addEventListener('pageshow', schedule, false);
        schedule();
    }

    return { init: init, render: render };
});
