window.downloadFile = (base64, filename) => {
    const link = document.createElement('a');
    link.href = 'data:text/csv;base64,' + base64;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};
