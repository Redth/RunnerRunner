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
                background: options?.darkMode ? '#1a1b26' : '#f8f9fa',
                foreground: options?.darkMode ? '#c0caf5' : '#24292f',
                cursor: options?.darkMode ? '#c0caf5' : '#24292f',
                selectionBackground: options?.darkMode ? '#33467c' : '#b6d7ff',
                black: '#15161e',
                red: '#f7768e',
                green: '#9ece6a',
                yellow: '#e0af68',
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
            fontFamily: "'SF Mono', 'Cascadia Code', 'Fira Code', 'Consolas', monospace",
            fontSize: 13,
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

        // Refit on resize
        var resizeObserver = new ResizeObserver(function () {
            try { fitAddon.fit(); } catch (e) { }
        });
        resizeObserver.observe(el);

        this._instances[elementId] = {
            term: term,
            fitAddon: fitAddon,
            searchAddon: searchAddon,
            resizeObserver: resizeObserver
        };

        return true;
    },

    write: function (elementId, text) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.term.write(text);
    },

    writeln: function (elementId, text) {
        var inst = this._instances[elementId];
        if (!inst) return;
        inst.term.writeln(text);
    },

    clear: function (elementId) {
        var inst = this._instances[elementId];
        if (!inst) return;
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
            background: darkMode ? '#1a1b26' : '#f8f9fa',
            foreground: darkMode ? '#c0caf5' : '#24292f',
            cursor: darkMode ? '#c0caf5' : '#24292f',
            selectionBackground: darkMode ? '#33467c' : '#b6d7ff'
        };
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

// Resizable table columns
window.rrResizableColumns = {
    init: function () {
        document.querySelectorAll('table.resizable-table').forEach(function (table) {
            if (table.dataset.resizable) return;
            table.dataset.resizable = 'true';
            table.style.tableLayout = 'fixed';

            var headers = table.querySelectorAll('thead th');
            headers.forEach(function (th, index) {
                if (index >= headers.length - 1) return;

                th.style.position = 'relative';
                th.style.overflow = 'hidden';
                th.style.textOverflow = 'ellipsis';

                var grip = document.createElement('div');
                grip.className = 'col-resize-grip';
                grip.addEventListener('mousedown', function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var startX = e.clientX;
                    var startW = th.getBoundingClientRect().width;
                    document.body.style.cursor = 'col-resize';
                    document.body.style.userSelect = 'none';
                    grip.classList.add('active');

                    function onMove(ev) {
                        var newW = Math.max(50, startW + ev.clientX - startX);
                        th.style.width = newW + 'px';
                    }
                    function onUp() {
                        document.removeEventListener('mousemove', onMove);
                        document.removeEventListener('mouseup', onUp);
                        document.body.style.cursor = '';
                        document.body.style.userSelect = '';
                        grip.classList.remove('active');
                    }
                    document.addEventListener('mousemove', onMove);
                    document.addEventListener('mouseup', onUp);
                });
                th.appendChild(grip);
            });
        });
    }
};
