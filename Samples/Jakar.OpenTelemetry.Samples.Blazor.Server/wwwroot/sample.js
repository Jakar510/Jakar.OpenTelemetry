// Helpers that raise browser-level failures outside .NET, which jakar-otel.js reports.
window.jakarSample = {
    throwError: () => setTimeout(() => { throw new TypeError('Sample JavaScript error'); }),
    rejectPromise: () => { Promise.reject(new Error('Sample unhandled promise rejection')); }
};
