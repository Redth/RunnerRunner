(function () {
    function popupIdFor(input) {
        var controls = input && input.getAttribute('aria-controls');
        return controls && controls.endsWith('-list')
            ? controls.substring(0, controls.length - 5)
            : null;
    }

    function openAutocomplete(input) {
        if (!input || !input.closest('.rr-autocomplete') || !window.Radzen) return;

        var popupId = popupIdFor(input);
        if (!popupId) return;

        function ensureVisible() {
            var popup = document.getElementById(popupId);
            if (!popup || document.activeElement !== input) return;

            if (window.getComputedStyle(popup).display !== 'none' && !popup.classList.contains('rz-close')) return;

            var rect = input.getBoundingClientRect();
            popup.style.display = 'block';
            popup.style.visibility = 'visible';
            popup.style.width = rect.width + 'px';
            popup.style.minWidth = rect.width + 'px';
            popup.style.left = rect.left + window.scrollX + 'px';
            popup.style.top = rect.bottom + window.scrollY + 'px';
            popup.style.zIndex = '2000';
            popup.classList.remove('rz-close');
            popup.classList.add('rz-open');
            input.setAttribute('aria-expanded', 'true');
        }

        function open() {
            if (document.activeElement !== input || !window.Radzen) return;
            window.Radzen.openPopup(input, popupId, true, null, null, null, null, null, false);
            window.setTimeout(ensureVisible, 20);
        }

        window.setTimeout(open, 80);
        window.setTimeout(open, 220);
        window.setTimeout(ensureVisible, 360);
        window.setTimeout(ensureVisible, 520);
    }

    function closeAutocomplete(input) {
        var popupId = popupIdFor(input);
        if (!popupId) return;

        window.setTimeout(function () {
            var popup = document.getElementById(popupId);
            if (!popup || document.activeElement === input || popup.contains(document.activeElement)) return;

            popup.style.display = 'none';
            popup.classList.remove('rz-open');
            popup.classList.add('rz-close');
            input.setAttribute('aria-expanded', 'false');
        }, 150);
    }

    document.addEventListener('focusin', function (event) {
        openAutocomplete(event.target);
    });

    document.addEventListener('input', function (event) {
        openAutocomplete(event.target);
    });

    document.addEventListener('focusout', function (event) {
        if (event.target && event.target.closest('.rr-autocomplete')) {
            closeAutocomplete(event.target);
        }
    });
})();
