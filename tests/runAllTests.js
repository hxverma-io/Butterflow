const { execSync } = require('child_process');
const path = require('path');

const tests = [
    path.join(__dirname, 'unit', 'protocol.test.js'),
    path.join(__dirname, 'unit', 'security.test.js'),
    path.join(__dirname, 'unit', 'layout.test.js'),
    path.join(__dirname, 'unit', 'fileTransfer.test.js'),
    path.join(__dirname, 'integration', 'dualNode.test.js')
];

console.log('====================================================');
console.log('       BUTTERFLOW AUTOMATED TEST SUITE RUNNER       ');
console.log('====================================================');

let passed = 0;
let failed = 0;

for (const testFile of tests) {
    const name = path.basename(testFile);
    console.log(`\n▶ Executing: ${name}`);
    try {
        const out = execSync(`node "${testFile}"`, { stdio: 'inherit' });
        passed++;
    } catch (err) {
        console.error(`✖ Test FAILED: ${name}`);
        failed++;
    }
}

console.log('\n====================================================');
console.log(`TEST SUMMARY: ${passed} Passed, ${failed} Failed`);
console.log('====================================================');

if (failed > 0) {
    process.exit(1);
} else {
    process.exit(0);
}
