define([], function () {
    'use strict';

    const storageKey = 'strmassistant.settings.activeTab';
    let observer = null;
    let refreshTimer = null;

    const definitions = [
        { key: 'general', zh: '常规', en: 'General', labels: ['常规选项', '通用选项', 'General Options'] },
        { key: 'media', zh: '媒体信息', en: 'Media', labels: ['Strm Extract', '媒体信息提取', '媒体提取', 'MediaInfo Extract'] },
        { key: 'metadata', zh: '元数据', en: 'Metadata', labels: ['元数据增强', 'Metadata Enhance', 'Metadata Enhancement'] },
        { key: 'intro', zh: '片头片尾', en: 'Intro / Credits', labels: ['片头片尾检测', '片头/片尾检测', '片头片尾', 'Intro/Credits Detection', 'Intro Credits Detection'] },
        { key: 'experience', zh: '体验增强', en: 'Experience', labels: ['体验增强', 'Experience Enhance', 'Experience Enhancement'] },
        { key: 'about', zh: '关于', en: 'About', labels: ['关于', 'About'] }
    ];

    function normalize(value) {
        return (value || '').replace(/\s+/g, ' ').trim().toLowerCase();
    }

    function isVisible(element) {
        if (!element || !element.isConnected) return false;
        const style = window.getComputedStyle(element);
        return style.display !== 'none' && style.visibility !== 'hidden';
    }

    function isChinese() {
        const language = (document.documentElement.lang || navigator.language || '').toLowerCase();
        return language.startsWith('zh');
    }

    function currentPage() {
        const candidates = Array.from(document.querySelectorAll('.page, [data-role="page"], .view'))
            .filter(isVisible)
            .reverse();
        const matched = candidates.find(page => {
            const text = normalize(page.textContent);
            if (!text.includes('strm assistant')) return false;
            return definitions.filter(item => item.labels.some(label => text.includes(normalize(label)))).length >= 3;
        });
        if (matched) return matched;

        const bodyText = normalize(document.body && document.body.textContent);
        if (bodyText.includes('strm assistant')) return document.body;
        return null;
    }

    function headingCandidates(root) {
        return Array.from(root.querySelectorAll(
            'h1,h2,h3,h4,h5,legend,.sectionTitle,.detailSectionHeader,.formDialogHeaderTitle,.editorTitle,.fieldDescriptionTitle'
        )).filter(isVisible);
    }

    function findHeading(root, labels) {
        const normalizedLabels = labels.map(normalize);
        const headings = headingCandidates(root);
        return headings.find(element => {
            const text = normalize(element.textContent);
            return normalizedLabels.some(label => text === label || text.startsWith(label + ' ') || text.includes(label));
        }) || null;
    }

    function lowestCommonAncestor(elements, boundary) {
        if (!elements.length) return boundary;
        let node = elements[0];
        while (node && node !== boundary) {
            if (elements.every(element => node.contains(element))) return node;
            node = node.parentElement;
        }
        return boundary;
    }

    function directChildUnder(element, ancestor) {
        let node = element;
        while (node && node.parentElement && node.parentElement !== ancestor) node = node.parentElement;
        return node && node.parentElement === ancestor ? node : null;
    }

    function nearestSection(element, root) {
        let node = element;
        while (node && node !== root) {
            if (node.matches && node.matches('fieldset,.verticalSection,.detailSection,.editorSection,.formSection,.paperList')) {
                return node;
            }
            node = node.parentElement;
        }
        return element.parentElement;
    }

    function collectSections(root) {
        const found = definitions.map(definition => ({
            definition,
            heading: findHeading(root, definition.labels)
        })).filter(item => item.heading);

        if (found.length < 3) return [];

        const common = lowestCommonAncestor(found.map(item => item.heading), root);
        const sections = found.map(item => {
            let section = directChildUnder(item.heading, common);
            if (!section || section === common) section = nearestSection(item.heading, root);
            return { definition: item.definition, heading: item.heading, section };
        }).filter(item => item.section);

        const uniqueSections = new Set(sections.map(item => item.section));
        if (uniqueSections.size < 3) {
            return found.map(item => ({
                definition: item.definition,
                heading: item.heading,
                section: nearestSection(item.heading, root)
            })).filter(item => item.section);
        }
        return sections;
    }

    function ensureStyle() {
        if (document.getElementById('strmassistant-settings-tabs-style')) return;
        const style = document.createElement('style');
        style.id = 'strmassistant-settings-tabs-style';
        style.textContent = `
            .strmassistant-settings-tabs {
                display: flex;
                flex-wrap: wrap;
                align-items: center;
                gap: .55em;
                margin: .4em 0 1.25em;
                padding: .6em 0;
                position: sticky;
                top: 0;
                z-index: 20;
                background: var(--theme-background, inherit);
            }
            .strmassistant-settings-tab {
                border: 1px solid currentColor;
                border-radius: .35em;
                background: transparent;
                color: inherit;
                cursor: pointer;
                min-height: 2.6em;
                padding: .55em 1em;
                opacity: .68;
                font: inherit;
            }
            .strmassistant-settings-tab:hover,
            .strmassistant-settings-tab:focus-visible {
                opacity: 1;
            }
            .strmassistant-settings-tab.is-active {
                opacity: 1;
                font-weight: 600;
                box-shadow: inset 0 -3px currentColor;
            }
            .strmassistant-settings-section-hidden {
                display: none !important;
            }
            @media (max-width: 600px) {
                .strmassistant-settings-tabs {
                    overflow-x: auto;
                    flex-wrap: nowrap;
                    position: static;
                    padding-bottom: .8em;
                }
                .strmassistant-settings-tab {
                    flex: 0 0 auto;
                }
            }
        `;
        document.head.appendChild(style);
    }

    function setActive(root, tabs, key) {
        const available = tabs.map(item => item.definition.key);
        if (!available.includes(key)) key = available[0];
        if (!key) return;

        tabs.forEach(item => {
            const active = item.definition.key === key;
            item.section.classList.toggle('strmassistant-settings-section-hidden', !active);
            item.button.classList.toggle('is-active', active);
            item.button.setAttribute('aria-selected', active ? 'true' : 'false');
            item.button.tabIndex = active ? 0 : -1;
        });

        try { window.localStorage.setItem(storageKey, key); } catch (_) {}
        root.dataset.strmassistantActiveTab = key;
    }

    function buildTabs(root, sections) {
        const existing = root.querySelector('.strmassistant-settings-tabs');
        if (existing) return true;

        const unique = [];
        const used = new Set();
        sections.forEach(item => {
            if (!item.section || used.has(item.section)) return;
            used.add(item.section);
            unique.push(item);
        });
        if (unique.length < 3) return false;

        ensureStyle();
        const container = document.createElement('div');
        container.className = 'strmassistant-settings-tabs';
        container.setAttribute('role', 'tablist');
        container.setAttribute('aria-label', isChinese() ? 'Strm Assistant 功能栏目' : 'Strm Assistant settings sections');

        const tabs = unique.map(item => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'strmassistant-settings-tab';
            button.setAttribute('role', 'tab');
            button.textContent = isChinese() ? item.definition.zh : item.definition.en;
            button.addEventListener('click', () => {
                setActive(root, tabs, item.definition.key);
                container.scrollIntoView({ block: 'nearest' });
            });
            container.appendChild(button);
            return { ...item, button };
        });

        const firstSection = unique[0].section;
        firstSection.parentElement.insertBefore(container, firstSection);

        let saved = '';
        try { saved = window.localStorage.getItem(storageKey) || ''; } catch (_) {}
        setActive(root, tabs, saved || unique[0].definition.key);
        root.dataset.strmassistantTabsReady = 'true';
        return true;
    }

    function apply() {
        const root = currentPage();
        if (!root) return false;
        if (root.querySelector('.strmassistant-settings-tabs')) return true;
        const sections = collectSections(root);
        return buildTabs(root, sections);
    }

    function scheduleApply() {
        if (refreshTimer) window.clearTimeout(refreshTimer);
        refreshTimer = window.setTimeout(() => {
            refreshTimer = null;
            apply();
        }, 180);
    }

    function init() {
        apply();
        if (!observer) {
            observer = new MutationObserver(scheduleApply);
            observer.observe(document.documentElement, { childList: true, subtree: true });
        }
        window.addEventListener('hashchange', scheduleApply, false);
        window.addEventListener('popstate', scheduleApply, false);
        return true;
    }

    return { init: init, refresh: scheduleApply };
});
