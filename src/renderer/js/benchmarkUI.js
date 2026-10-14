// ButterFlow Benchmark and Performance Diagnostics Controller

class BenchmarkUI {
    constructor() {
        this.btnRun = document.getElementById('btnRunBenchmark');
        this.valMedian = document.getElementById('benchMedian');
        this.valP95 = document.getElementById('benchP95');
        this.valP99 = document.getElementById('benchP99');
        this.valDrop = document.getElementById('benchDrop');
        this.historyList = document.getElementById('benchmarkHistory');

        if (this.btnRun) {
            this.btnRun.addEventListener('click', () => this.runTest());
        }
    }

    async runTest() {
        if (this.btnRun) {
            this.btnRun.disabled = true;
            this.btnRun.textContent = 'Running Diagnostics...';
        }

        try {
            const res = await fetch('/api/benchmark/start', { method: 'POST' });
            const data = await res.json();
            this.displayResults(data);
        } catch (err) {
            alert('Benchmark failed: ' + err.message);
        } finally {
            if (this.btnRun) {
                this.btnRun.disabled = false;
                this.btnRun.textContent = 'Run Latency Benchmark';
            }
        }
    }

    displayResults(res) {
        if (this.valMedian) this.valMedian.textContent = `${res.median} ms`;
        if (this.valP95) this.valP95.textContent = `${res.p95} ms`;
        if (this.valP99) this.valP99.textContent = `${res.p99} ms`;
        if (this.valDrop) this.valDrop.textContent = res.dropRate || '0.0%';

        // Also update dashboard top cards
        const statLatency = document.getElementById('statLatency');
        const statMedian = document.getElementById('statMedian');
        if (statLatency) statLatency.textContent = `${res.p95} ms`;
        if (statMedian) statMedian.textContent = `${res.median} ms`;

        if (this.historyList) {
            const empty = this.historyList.querySelector('.empty-state');
            if (empty) empty.remove();

            const card = document.createElement('div');
            card.style.cssText = 'background:var(--bg-card); border:1px solid var(--border-color); border-radius:8px; padding:12px; margin-bottom:8px; display:flex; justify-content:space-between; align-items:center;';
            card.innerHTML = `
                <div>
                    <strong>${res.type || 'Latency Benchmark'}</strong>
                    <div style="font-size:11px; color:var(--text-muted);">${new Date(res.timestamp).toLocaleTimeString()} · ${res.sampleCount} samples</div>
                </div>
                <div style="text-align:right; font-family:var(--font-mono);">
                    <span style="color:var(--accent-amber); font-weight:700;">p50: ${res.median}ms</span> · 
                    <span>p95: ${res.p95}ms</span> · 
                    <span>p99: ${res.p99}ms</span> · 
                    <span style="color:var(--text-muted);">Jitter: ${res.jitter}ms</span>
                </div>
            `;
            this.historyList.insertBefore(card, this.historyList.firstChild);
        }
    }
}

window.BenchmarkUI = new BenchmarkUI();
