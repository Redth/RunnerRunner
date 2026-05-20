// RunnerRunner terminal.js — xterm.js interop for Blazor
window.rrTerminal = {
    _instances: {},

    init: function (elementId, options) {
        var el = document.getElementById(elementId);
        if (!el) return false;

        // Clean up existing instance
        if (this._instances[elementId]) {
            this._instances[elementId].term.dispose();
            delete this._instances[elementId];
        }

        var term = new window.Terminal({
            theme: {
                background: options?.darkMode ? '#05070b' : '#f8f9fa',
                foreground: options?.darkMode ? '#f3f4f6' : '#24292f',
                cursor: options?.darkMode ? '#f3f4f6' : '#24292f',
                selectionBackground: options?.darkMode ? '#374151' : '#b6d7ff',
                black: '#15161e',
                red: '#f7768e',
                green: '#22c55e',
                yellow: '#e0a100',
                blue: '#7aa2f7',
                magenta: '#bb9af7',
                cyan: '#7dcfff',
                white: '#a9b1d6',
                brightBlack: '#414868',
                brightRed: '#f7768e',
                brightGreen: '#9ece6a',
                brightYellow: '#e0af68',
                brightBlue: '#7aa2f7',
                brightMagenta: '#bb9af7',
                brightCyan: '#7dcfff',
                brightWhite: '#c0caf5'
            },
            fontFamily: "'JetBrains Mono', 'SF Mono', 'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            fontSize: options?.fontSize || 12,
            lineHeight: 1.4,
            scrollback: 5000,
            cursorBlink: false,
            cursorStyle: 'bar',
            disableStdin: true,
            convertEol: true
        });

        var fitAddon = new window.FitAddon.FitAddon();
        var searchAddon = new window.SearchAddon.SearchAddon();
        term.loadAddon(fitAddon);
        term.loadAddon(searchAddon);
        term.open(el);

        // Delay fit to ensure container has dimensions
        setTimeout(function () { fitAddon.fit(); }, 50);

        var instance = {
            term: term,
            fitAddon: fitAddon,
            searchAddon: searchAddon,
            resizeObserver: null,
            lastText: '',
            wrap: true
        };

        // Refit on resize unless wrapping is disabled and the terminal is using a wide buffer.
        var resizeObserver = new ResizeObserver(function () {
            try {
                if (instance.wrap === false) {
                    term.resize(240, Math.max(term.rows || 24, 1));
                } else {
                    fitAddon.fit();
                }
            } catch (e) { }
        });
        resizeObserver.observe(el);
        instance.resizeObserver = resizeObserver;

        this._instances[elementId] = instance;

        return true;
    },

    write: function (elementId, text) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.lastText += text || '';
        inst.term.write(text);
    },

    writeln: function (elementId, text) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.lastText += (text || '') + '\n';
        inst.term.writeln(text);
    },

    writeText: function (elementId, text, autoScroll) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.lastText = text || '';
        inst.term.clear();
        inst.term.reset();
        if (inst.wrap === false) {
            inst.term.resize(240, Math.max(inst.term.rows || 24, 1));
        }
        inst.term.write(inst.lastText.replace(/\r?\n/g, '\r\n'));
        if (autoScroll !== false) {
            setTimeout(function () { inst.term.scrollToBottom(); }, 0);
        }
    },

    clear: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.lastText = '';
        inst.term.clear();
        inst.term.reset();
    },

    search: function (elementId, query) {
        var inst = this._instances[elementId];
        if (!inst) return false;
        return inst.searchAddon.findNext(query, { regex: false, caseSensitive: false });
    },

    searchPrevious: function (elementId, query) {
        var inst = this._instances[elementId];
        if (!inst) return false;
        return inst.searchAddon.findPrevious(query, { regex: false, caseSensitive: false });
    },

    setTheme: function (elementId, darkMode) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.term.options.theme = {
            background: darkMode ? '#05070b' : '#f8f9fa',
            foreground: darkMode ? '#f3f4f6' : '#24292f',
            cursor: darkMode ? '#f3f4f6' : '#24292f',
            selectionBackground: darkMode ? '#374151' : '#b6d7ff'
        };
    },

    setFontSize: function (elementId, fontSize) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.term.options.fontSize = Number(fontSize) || 12;
        if (inst.wrap === false) {
            inst.term.resize(240, Math.max(inst.term.rows || 24, 1));
        } else {
            inst.fitAddon.fit();
        }
    },

    setWrap: function (elementId, wrap) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.wrap = wrap !== false;
        if (inst.wrap) {
            inst.fitAddon.fit();
        } else {
            inst.term.resize(240, Math.max(inst.term.rows || 24, 1));
        }
    },

    scrollToBottom: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.term.scrollToBottom();
    },

    copy: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst || !navigator.clipboard) return;
        navigator.clipboard.writeText(inst.lastText || '');
    },

    download: function (elementId, filename) {
        var inst = this._instances[elementId];
        if (!inst) return;
        var blob = new Blob([inst.lastText || ''], { type: 'text/plain;charset=utf-8' });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename || 'runnerrunner-logs.log';
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
    },

    fit: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.fitAddon.fit();
    },

    dispose: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.resizeObserver.disconnect();
        inst.term.dispose();
        delete this._instances[elementId];
    }
};

// Resizable split-pane
window.rrSplitPane = {
    init: function (handleId, panelId, storageKey, minWidth, maxWidth) {
        var handle = document.getElementById(handleId);
        var panel = document.getElementById(panelId);
        if (!handle || !panel) return;

        minWidth = minWidth || 180;
        maxWidth = maxWidth || 500;

        // Restore saved width
        var saved = localStorage.getItem(storageKey);
        if (saved) {
            var w = parseInt(saved, 10);
            if (w >= minWidth && w <= maxWidth) panel.style.width = w + 'px';
        }

        var startX, startW;
        handle.addEventListener('mousedown', function (e) {
            e.preventDefault();
            startX = e.clientX;
            startW = panel.getBoundingClientRect().width;
            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            function onMove(ev) {
                var newW = Math.min(maxWidth, Math.max(minWidth, startW + ev.clientX - startX));
                panel.style.width = newW + 'px';
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                localStorage.setItem(storageKey, Math.round(panel.getBoundingClientRect().width));
                // Refit terminal if present
                if (window.rrTerminal && window.rrTerminal._instances['rr-terminal']) {
                    window.rrTerminal.fit('rr-terminal');
                }
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }
};
