window.downloadFile = (base64, filename) => {
    const link = document.createElement('a');
    link.href = 'data:text/csv;base64,' + base64;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Thousands separators for .money-input boxes, applied while typing.
// Runs in the capture phase so Blazor reads the already-grouped text; the caret stays after the same digit.
(function () {
    const toLatin = s => s.replace(/[۰-۹]/g, d => String.fromCharCode(d.charCodeAt(0) - 1728))
                          .replace(/[٠-٩]/g, d => String.fromCharCode(d.charCodeAt(0) - 1584))
                          .replace(/٫/g, '.');
    const group = raw => {
        let s = toLatin(raw).replace(/[^\d.]/g, '');
        const dot = s.indexOf('.');
        let int = dot >= 0 ? s.slice(0, dot) : s;
        const frac = dot >= 0 ? '.' + s.slice(dot + 1).replace(/\./g, '') : '';
        int = int.replace(/^0+(?=\d)/, '');
        return int.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + frac;
    };
    window.addEventListener('input', e => {
        const el = e.target;
        if (!el || !el.classList || !el.classList.contains('money-input')) return;
        const caret = el.selectionStart ?? el.value.length;
        const digitsBefore = toLatin(el.value.slice(0, caret)).replace(/[^\d.]/g, '').length;
        const formatted = group(el.value);
        if (formatted === el.value) return;
        el.value = formatted;
        let pos = 0, seen = 0;
        while (pos < formatted.length && seen < digitsBefore) { if (formatted[pos] !== ',') seen++; pos++; }
        el.setSelectionRange(pos, pos);
    }, true);
})();

// In-app history: each page switch adds a browser history entry, so Back returns to the previous page.
// The first entry is a guard that keeps Back from leaving the app.
window.appNav = {
    ref: null,
    init(ref, page) {
        this.ref = ref;
        history.replaceState({ appGuard: true, page }, '');
        history.pushState({ page }, '');
        if (this.bound) return;
        this.bound = true;
        window.addEventListener('popstate', e => {
            const s = e.state || {};
            if (s.appGuard) history.pushState({ page: s.page }, '');
            if (s.page && this.ref) this.ref.invokeMethodAsync('OnBrowserBack', s.page);
        });
    },
    push(page) { history.pushState({ page }, ''); }
};
