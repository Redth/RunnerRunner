// Adaptive table grid sizing for Blazor-rendered tables.
// Keeps native table semantics and Blazor event handlers while adding colgroup sizing,
// column resize handles, and priority-based column snapping on narrow containers.
(function () {
    var tableStates = new WeakMap();
    var resizeObserver = typeof ResizeObserver !== 'undefined'
        ? new ResizeObserver(function (entries) {
            entries.forEach(function (entry) {
                var table = entry.target.querySelector('table.rr-data-grid-table');
                if (table) scheduleLayout(table);
            });
        })
        : null;

    function cleanText(value) {
        return (value || '').replace(/\s+/g, ' ').trim();
    }

    function toNumber(value, fallback) {
        var parsed = parseFloat(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function clamp(value, min, max) {
        return Math.min(max, Math.max(min, value));
    }

    function getHeaderText(th) {
        var clone = th.cloneNode(true);
        clone.querySelectorAll('.col-resize-grip').forEach(function (grip) { grip.remove(); });
        return cleanText(clone.textContent).replace(/[▲▼]/g, '').trim().toLowerCase();
    }

    function getColumnCells(table, index) {
        return Array.from(table.tBodies)
            .flatMap(function (tbody) { return Array.from(tbody.rows); })
            .map(function (row) { return row.children[index]; })
            .filter(Boolean);
    }

    function columnHasClass(table, index, className) {
        return getColumnCells(table, index).some(function (cell) {
            return cell.classList.contains(className);
        });
    }

    function getStorageKey(table, index, headerText) {
        var tableIndex = Array.from(document.querySelectorAll('table.rr-data-grid-table')).indexOf(table);
        return 'rr-table-col:' + location.pathname + ':' + tableIndex + ':' + index + ':' + headerText;
    }

    function readStoredWidth(table, index, headerText) {
        try {
            return toNumber(localStorage.getItem(getStorageKey(table, index, headerText)), null);
        } catch (e) {
            return null;
        }
    }

    function writeStoredWidth(table, index, headerText, width) {
        try {
            localStorage.setItem(getStorageKey(table, index, headerText), String(Math.round(width)));
        } catch (e) { }
    }

    function estimateTextWidth(text) {
        return Math.min(420, Math.max(0, cleanText(text).length * 6.6 + 28));
    }

    function measureColumnContent(table, index, fallback) {
        var cells = getColumnCells(table, index).slice(0, 12);
        var max = fallback;
        cells.forEach(function (cell) {
            if (cell.colSpan && cell.colSpan > 1) return;
            max = Math.max(max, Math.ceil(cell.scrollWidth + 8));
        });
        return max;
    }

    function getCellHorizontalPadding(cell) {
        var style = getComputedStyle(cell);
        return toNumber(style.paddingLeft, 0) + toNumber(style.paddingRight, 0) + 4;
    }

    function measureActionContent(table, index, fallback) {
        var cells = getColumnCells(table, index).slice(0, 12);
        var max = fallback;
        cells.forEach(function (cell) {
            if (cell.colSpan && cell.colSpan > 1) return;
            var gutter = getCellHorizontalPadding(cell);

            var groupedActions = cell.querySelector(':scope > .host-actions, :scope > .action-group, :scope > .btn-group');
            if (groupedActions) {
                max = Math.max(max, Math.ceil(groupedActions.getBoundingClientRect().width + gutter));
                return;
            }

            var visibleChildren = Array.from(cell.children).filter(function (child) {
                var style = getComputedStyle(child);
                return style.display !== 'none' && style.position !== 'absolute';
            });

            if (!visibleChildren.length) {
                max = Math.max(max, Math.ceil(cell.scrollWidth + 4));
                return;
            }

            var left = Infinity;
            var right = -Infinity;
            visibleChildren.forEach(function (child) {
                var rect = child.getBoundingClientRect();
                if (rect.width <= 0) return;
                left = Math.min(left, rect.left);
                right = Math.max(right, rect.right);
            });

            if (Number.isFinite(left) && Number.isFinite(right)) {
                max = Math.max(max, Math.ceil(right - left + gutter));
            }
        });
        return max;
    }

    function inferSpec(table, th, index, count) {
        var text = getHeaderText(th);
        var hasNameCell = columnHasClass(table, index, 'name-cell');
        var isActions = th.classList.contains('actions-col') || text === 'actions' || (text === '' && index === count - 1);
        var isControl = text === '' || text === ' ';
        var actionContentWidth = isActions ? measureActionContent(table, index, 84) : 0;
        var attrMin = th.dataset.colMin;
        var attrPreferred = th.dataset.colPreferred;
        var attrMax = th.dataset.colMax;
        var attrPriority = th.dataset.colPriority;
        var attrWeight = th.dataset.colWeight;
        var kind = th.dataset.colKind || 'default';
        var min = 92;
        var preferred = 136;
        var max = 260;
        var priority = 3;
        var weight = 1;

        if (isActions) {
            kind = 'actions';
            min = 56;
            preferred = actionContentWidth;
            max = 260;
            priority = 1;
            weight = 0;
        } else if (isControl) {
            kind = 'control';
            min = 34;
            preferred = 38;
            max = 44;
            priority = 1;
            weight = 0;
        } else if (hasNameCell) {
            kind = 'name';
            min = 155;
            preferred = Math.max(210, measureColumnContent(table, index, estimateTextWidth(text)));
            max = 440;
            priority = 1;
            weight = 3.5;
        } else if (/^(status|mode|type|time|size|stars|priority|default|cached\??|duration)$/.test(text)) {
            kind = 'compact';
            min = 66;
            preferred = Math.max(86, estimateTextWidth(text));
            max = 132;
            priority = 2;
            weight = 0.35;
        } else if (/created|updated|started|heartbeat|health check|last reported/.test(text)) {
            kind = 'time';
            min = 98;
            preferred = 126;
            max = 156;
            priority = 3;
            weight = 0.45;
        } else if (/labels|image tag|instance|image id|cached on/.test(text)) {
            kind = 'optional';
            min = 94;
            preferred = 128;
            max = 220;
            priority = 5;
            weight = 0.8;
        } else if (/description|details/.test(text)) {
            kind = 'description';
            min = 140;
            preferred = 220;
            max = 380;
            priority = 4;
            weight = 2;
        } else if (/worker|platform|profile|rule|target|provisioning/.test(text)) {
            kind = 'related';
            min = 110;
            preferred = 154;
            max = 260;
            priority = 4;
            weight = 1;
        }

        min = toNumber(attrMin, min);
        preferred = toNumber(attrPreferred, preferred);
        max = toNumber(attrMax, max);
        priority = toNumber(attrPriority, priority);
        weight = toNumber(attrWeight, weight);

        preferred = clamp(preferred, min, Math.max(min, max));
        max = Math.max(max, preferred);

        if (kind === 'actions') {
            min = Math.max(min, Math.min(actionContentWidth, max));
            preferred = Math.max(preferred, min);
            max = Math.max(max, preferred);
        }

        var storedWidth = readStoredWidth(table, index, text);
        return {
            index: index,
            headerText: text,
            kind: kind,
            min: min,
            preferred: preferred,
            max: max,
            priority: priority,
            weight: weight,
            userWidth: storedWidth && storedWidth >= min ? storedWidth : null,
            visible: true
        };
    }

    function ensureColGroup(table, count) {
        var colgroup = table.querySelector(':scope > colgroup');
        if (!colgroup) {
            colgroup = document.createElement('colgroup');
            table.insertBefore(colgroup, table.firstChild);
        }
        while (colgroup.children.length < count) {
            colgroup.appendChild(document.createElement('col'));
        }
        while (colgroup.children.length > count) {
            colgroup.lastElementChild.remove();
        }
        return colgroup;
    }

    function setColumnVisible(table, col, visible) {
        var colElement = table.querySelector('colgroup')?.children[col.index];
        if (colElement) colElement.style.display = visible ? '' : 'none';
        table.querySelectorAll('tr').forEach(function (row) {
            var cell = row.children[col.index];
            if (!cell || (cell.colSpan && cell.colSpan > 1)) return;
            cell.style.display = visible ? '' : 'none';
            cell.classList.toggle('rr-col-hidden', !visible);
        });
    }

    function distributeWidths(cols, availableWidth) {
        var visible = cols.filter(function (col) { return col.visible; });
        var widths = new Map();
        var base = 0;
        visible.forEach(function (col) {
            var width = col.userWidth ? clamp(col.userWidth, col.min, Math.max(col.max, col.userWidth)) : col.min;
            widths.set(col.index, width);
            base += width;
        });

        var leftover = Math.max(0, availableWidth - base);
        var growable = visible.filter(function (col) {
            return !col.userWidth && col.weight > 0 && widths.get(col.index) < col.max;
        });

        while (leftover > 0.5 && growable.length) {
            var totalWeight = growable.reduce(function (sum, col) { return sum + col.weight; }, 0) || 1;
            var used = 0;
            growable.forEach(function (col) {
                var current = widths.get(col.index);
                var share = leftover * (col.weight / totalWeight);
                var next = Math.min(col.max, current + share);
                used += next - current;
                widths.set(col.index, next);
            });
            leftover -= used;
            growable = growable.filter(function (col) { return widths.get(col.index) < col.max - 0.5; });
            if (used <= 0.5) break;
        }

        if (leftover > 0.5) {
            var stretchable = visible.filter(function (col) {
                return !col.userWidth && col.weight > 0 && col.kind !== 'actions' && col.kind !== 'control' && col.kind !== 'compact';
            });
            var totalStretch = stretchable.reduce(function (sum, col) { return sum + col.weight; }, 0) || 1;
            stretchable.forEach(function (col) {
                widths.set(col.index, widths.get(col.index) + leftover * (col.weight / totalStretch));
            });
        }

        return widths;
    }

    function snapWidthsToTarget(cols, widths, targetWidth) {
        var visible = cols.filter(function (col) { return col.visible; });
        var snapped = new Map();
        var total = 0;
        var candidates = [];

        visible.forEach(function (col) {
            var rawWidth = Math.max(col.min, widths.get(col.index) || col.min);
            var snappedWidth = Math.floor(rawWidth);
            snapped.set(col.index, snappedWidth);
            total += snappedWidth;
            candidates.push({
                col: col,
                remainder: rawWidth - snappedWidth
            });
        });

        var remaining = Math.max(0, Math.round(targetWidth) - total);
        candidates.sort(function (a, b) {
            return b.remainder - a.remainder || b.col.weight - a.col.weight || a.col.index - b.col.index;
        });

        for (var i = 0; remaining > 0 && candidates.length; i = (i + 1) % candidates.length) {
            var candidate = candidates[i];
            var current = snapped.get(candidate.col.index) || 0;
            snapped.set(candidate.col.index, current + 1);
            remaining--;
        }

        return snapped;
    }

    function layoutTable(table) {
        var state = tableStates.get(table);
        if (!state) return;

        var wrapper = table.closest('.table-responsive') || table.parentElement;
        var containerWidth = Math.floor(wrapper?.clientWidth || table.parentElement?.clientWidth || table.clientWidth || 0);
        if (containerWidth <= 0) return;

        state.cols.forEach(function (col) { col.visible = true; });
        var minSum = state.cols.reduce(function (sum, col) { return sum + col.min; }, 0);
        var hideCandidates = state.cols
            .filter(function (col) { return col.priority >= 4 && col.kind !== 'actions' && col.kind !== 'name'; })
            .sort(function (a, b) { return b.priority - a.priority || b.index - a.index; });

        hideCandidates.forEach(function (col) {
            if (minSum <= containerWidth) return;
            col.visible = false;
            minSum -= col.min;
        });

        if (minSum > containerWidth && containerWidth < 980) {
            state.cols
                .filter(function (col) { return col.priority === 3 && col.kind !== 'name' && col.kind !== 'actions'; })
                .sort(function (a, b) { return b.index - a.index; })
                .forEach(function (col) {
                    if (minSum <= containerWidth) return;
                    col.visible = false;
                    minSum -= col.min;
                });
        }

        state.cols.forEach(function (col) { setColumnVisible(table, col, col.visible); });

        var visibleMin = state.cols
            .filter(function (col) { return col.visible; })
            .reduce(function (sum, col) { return sum + col.min; }, 0);
        var targetWidth = Math.max(containerWidth, visibleMin);
        var widths = distributeWidths(state.cols, targetWidth);
        var colgroup = ensureColGroup(table, state.cols.length);
        var rawTotalWidth = 0;

        state.cols.forEach(function (col) {
            var width = col.visible ? Math.max(col.min, widths.get(col.index) || col.min) : 0;
            if (col.visible) rawTotalWidth += width;
        });

        targetWidth = Math.max(containerWidth, Math.round(rawTotalWidth));
        widths = snapWidthsToTarget(state.cols, widths, targetWidth);
        var totalWidth = 0;

        state.cols.forEach(function (col) {
            var width = col.visible ? Math.max(0, widths.get(col.index) || 0) : 0;
            var colElement = colgroup.children[col.index];
            if (colElement) colElement.style.width = col.visible ? width + 'px' : '0';
            if (col.visible) totalWidth += width;
        });

        table.style.width = Math.max(containerWidth, totalWidth) + 'px';
        table.style.minWidth = Math.ceil(visibleMin) + 'px';
    }

    function scheduleLayout(table) {
        var state = tableStates.get(table);
        if (!state || state.pending) return;
        state.pending = true;
        requestAnimationFrame(function () {
            state.pending = false;
            state.cols = Array.from(table.querySelectorAll('thead th')).map(function (th, index, headers) {
                var existing = state.cols[index];
                var next = inferSpec(table, th, index, headers.length);
                if (existing?.userWidth && existing.headerText === next.headerText && existing.kind === next.kind) {
                    next.userWidth = existing.userWidth;
                }
                return next;
            });
            layoutTable(table);
        });
    }

    function addGrip(table, th, col) {
        if (th.querySelector(':scope > .col-resize-grip') || col.kind === 'control') return;

        th.style.position = 'relative';
        var grip = document.createElement('div');
        grip.className = 'col-resize-grip';
        grip.setAttribute('role', 'separator');
        grip.setAttribute('aria-orientation', 'vertical');
        grip.setAttribute('aria-label', 'Resize ' + (col.headerText || 'column') + ' column');

        grip.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();

            var state = tableStates.get(table);
            if (!state) return;
            var liveCol = state.cols[col.index];
            var startX = e.clientX;
            var startWidth = th.getBoundingClientRect().width;

            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';
            grip.classList.add('active');

            function onMove(ev) {
                var nextWidth = Math.max(liveCol.min, startWidth + ev.clientX - startX);
                liveCol.userWidth = nextWidth;
                layoutTable(table);
            }

            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                grip.classList.remove('active');
                writeStoredWidth(table, liveCol.index, liveCol.headerText, liveCol.userWidth || startWidth);
            }

            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });

        th.appendChild(grip);
    }

    function enhanceTable(table) {
        var wrapper = table.closest('.table-responsive');
        if (wrapper) {
            wrapper.classList.add('rr-data-grid');
            if (resizeObserver && !wrapper.dataset.rrGridObserved) {
                wrapper.dataset.rrGridObserved = 'true';
                resizeObserver.observe(wrapper);
            }
        }

        table.classList.add('rr-data-grid-table', 'resizable-table');
        var headers = Array.from(table.querySelectorAll('thead th'));
        if (!headers.length) return;

        var existing = tableStates.get(table);
        var cols = headers.map(function (th, index) { return inferSpec(table, th, index, headers.length); });
        tableStates.set(table, { cols: existing?.cols ?? cols, pending: false });
        tableStates.get(table).cols = cols;

        ensureColGroup(table, headers.length);
        headers.forEach(function (th, index) {
            addGrip(table, th, cols[index]);
        });
        scheduleLayout(table);
    }

    window.rrResizableColumns = {
        init: function () {
            document
                .querySelectorAll('table.resizable-table, .table-responsive > table.table:not(.table-sm)')
                .forEach(enhanceTable);
        },
        relayout: function () {
            document.querySelectorAll('table.rr-data-grid-table').forEach(scheduleLayout);
        }
    };

    window.addEventListener('resize', function () {
        if (window.rrResizableColumns) window.rrResizableColumns.relayout();
    });
})();
