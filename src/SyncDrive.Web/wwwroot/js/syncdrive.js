// Kleine Helfer, die Blazor nicht selbst kann.
window.syncDrive = {
    // „Ordner hochladen“: Pfad jeder gewählten Datei relativ zum gewählten Ordner („Fotos/2026/a.jpg“), in der Reihenfolge von input.files.
    relativePaths: input => Array.from(input.files, f => f.webkitRelativePath || f.name),
    // Tastenkürzel öffnen die Dateiauswahl wie ein Klick (Element per id, damit es auch außerhalb der Komponente gefunden wird).
    click: id => document.getElementById(id)?.click(),
    // Doppelklick auf eine Datei, die sich nicht anzeigen lässt: herunterladen wie über den Knopf.
    download: (url, name) => {
        const a = document.createElement("a");
        a.href = url;
        a.download = name;
        document.body.appendChild(a);
        a.click();
        a.remove();
    },
    // Hell/Dunkel: Wahl „system“, „light“ oder „dark“ je Browser; am html-Element steht immer das Ergebnis (light/dark), bei „system“ nach
    // der Einstellung des Betriebssystems, auch wenn sie sich ändert. Ohne Speicher (privates Fenster) gilt „system“.
    theme: {
        key: "syncdrive.theme",
        dark: window.matchMedia("(prefers-color-scheme: dark)"),
        get() {
            try { return localStorage.getItem(this.key) || "system"; } catch { return "system"; }
        },
        set(choice) {
            try { localStorage.setItem(this.key, choice); } catch { /* nur für diese Sitzung */ }
            this.apply(choice);
        },
        apply(choice = this.get()) {
            const dark = choice === "dark" || (choice === "system" && this.dark.matches);
            document.documentElement.dataset.theme = dark ? "dark" : "light";
        },
    },
};
syncDrive.theme.apply();
syncDrive.theme.dark.addEventListener("change", () => syncDrive.theme.apply());
