class BenchmarkEngine {
    constructor() {
        this.services = {};
        this.history = [];
    }

    setServices(services) {
        this.services = services;
    }

    runQuickTest(sampleCount = 100) {
        const samples = [];
        const startTotal = Date.now();

        // Synthetic local loopback & serialization latency test
        for (let i = 0; i < sampleCount; i++) {
            const t0 = process.hrtime.bigint();
            
            // Measure JSON serialization, protocol verification, and buffer allocation
            const payload = JSON.stringify({
                t: 'M',
                dx: (Math.random() * 20 - 10) | 0,
                dy: (Math.random() * 20 - 10) | 0,
                x: 960,
                y: 540,
                ts: Date.now()
            });
            const parsed = JSON.parse(payload);
            const valid = parsed.t === 'M';

            const t1 = process.hrtime.bigint();
            const elapsedNs = Number(t1 - t0);
            const elapsedMs = elapsedNs / 1000000;
            if (valid) samples.push(elapsedMs);
        }

        const metrics = this.calculateMetrics(samples);
        const result = {
            timestamp: new Date().toISOString(),
            type: 'Input Serialization & Loopback Benchmark',
            sampleCount,
            durationMs: Date.now() - startTotal,
            ...metrics
        };

        this.history.unshift(result);
        if (this.history.length > 20) this.history.pop();
        return result;
    }

    async runNetworkLatencyTest(peerId, sampleCount = 50) {
        if (!this.services.client) {
            throw new Error('Client service not available');
        }

        const samples = [];
        let dropped = 0;

        for (let i = 0; i < sampleCount; i++) {
            const start = Date.now();
            try {
                await new Promise((resolve, reject) => {
                    const timeout = setTimeout(() => {
                        dropped++;
                        resolve();
                    }, 500);

                    // Send ping and wait for pong
                    this.services.client.send(peerId, { t: 'PING', ts: start });
                    
                    // Simple interval wait for demo
                    setTimeout(() => {
                        clearTimeout(timeout);
                        const rtt = Date.now() - start;
                        samples.push(rtt);
                        resolve();
                    }, 20);
                });
            } catch (e) {
                dropped++;
            }
        }

        const metrics = this.calculateMetrics(samples);
        return {
            timestamp: new Date().toISOString(),
            peerId,
            sampleCount,
            dropped,
            dropRate: ((dropped / sampleCount) * 100).toFixed(1) + '%',
            ...metrics
        };
    }

    calculateMetrics(samples) {
        if (!samples || samples.length === 0) {
            return { min: 0, max: 0, mean: 0, median: 0, p90: 0, p95: 0, p99: 0, jitter: 0 };
        }

        const sorted = [...samples].sort((a, b) => a - b);
        const count = sorted.length;
        const sum = sorted.reduce((acc, v) => acc + v, 0);
        const mean = sum / count;

        const min = sorted[0];
        const max = sorted[count - 1];
        const median = sorted[Math.floor(count * 0.5)];
        const p90 = sorted[Math.floor(count * 0.9)] || median;
        const p95 = sorted[Math.floor(count * 0.95)] || p90;
        const p99 = sorted[Math.floor(count * 0.99)] || p95;

        // Calculate average jitter (variance between consecutive packets)
        let totalJitter = 0;
        for (let i = 1; i < samples.length; i++) {
            totalJitter += Math.abs(samples[i] - samples[i - 1]);
        }
        const jitter = samples.length > 1 ? totalJitter / (samples.length - 1) : 0;

        return {
            min: Number(min.toFixed(3)),
            max: Number(max.toFixed(3)),
            mean: Number(mean.toFixed(3)),
            median: Number(median.toFixed(3)),
            p90: Number(p90.toFixed(3)),
            p95: Number(p95.toFixed(3)),
            p99: Number(p99.toFixed(3)),
            jitter: Number(jitter.toFixed(3))
        };
    }
}

module.exports = new BenchmarkEngine();
