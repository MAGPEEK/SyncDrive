// Kleine Helfer, die Blazor nicht selbst kann.
window.syncDrive = {
    // „Ordner hochladen“: Pfad jeder gewählten Datei relativ zum gewählten Ordner („Fotos/2026/a.jpg“), in der Reihenfolge von input.files.
    relativePaths: input => Array.from(input.files, f => f.webkitRelativePath || f.name),
    // Tastenkürzel öffnen die Dateiauswahl wie ein Klick (Element per id, damit es auch außerhalb der Komponente gefunden wird).
    click: id => document.getElementById(id)?.click(),
};
