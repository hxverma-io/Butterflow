const assert = require('assert');

console.log('[Test] Running Layout & Edge Transition Math Unit Tests...');

function detectEdgeHit(x, y, screenWidth, screenHeight, threshold = 2) {
    if (x >= screenWidth - threshold) return 'right';
    if (x <= threshold) return 'left';
    if (y <= threshold) return 'top';
    if (y >= screenHeight - threshold) return 'bottom';
    return null;
}

function calculateEntryCoordinate(sourceEdge, ratio, targetWidth, targetHeight) {
    // If exiting through right edge of PC A, enters at left edge of PC B
    if (sourceEdge === 'right') {
        return { x: 0, y: Math.round(ratio * targetHeight) };
    }
    // If exiting through left edge of PC A, enters at right edge of PC B
    if (sourceEdge === 'left') {
        return { x: targetWidth - 1, y: Math.round(ratio * targetHeight) };
    }
    // If exiting through top edge of PC A, enters at bottom edge of PC B
    if (sourceEdge === 'top') {
        return { x: Math.round(ratio * targetWidth), y: targetHeight - 1 };
    }
    // If exiting through bottom edge of PC A, enters at top edge of PC B
    if (sourceEdge === 'bottom') {
        return { x: Math.round(ratio * targetWidth), y: 0 };
    }
    return { x: 0, y: 0 };
}

// 1. Edge Hit Detection
assert.strictEqual(detectEdgeHit(1919, 540, 1920, 1080, 2), 'right');
assert.strictEqual(detectEdgeHit(1, 540, 1920, 1080, 2), 'left');
assert.strictEqual(detectEdgeHit(960, 1, 1920, 1080, 2), 'top');
assert.strictEqual(detectEdgeHit(960, 1079, 1920, 1080, 2), 'bottom');
assert.strictEqual(detectEdgeHit(960, 540, 1920, 1080, 2), null);

// 2. Cross-Screen Coordinate Mapping
// Exiting right at 50% vertical height -> enters remote left at 50% vertical height
const entryRight = calculateEntryCoordinate('right', 0.5, 2560, 1440);
assert.strictEqual(entryRight.x, 0);
assert.strictEqual(entryRight.y, 720);

// Exiting top at 25% horizontal width -> enters remote bottom at 25% horizontal width
const entryTop = calculateEntryCoordinate('top', 0.25, 1920, 1080);
assert.strictEqual(entryTop.x, 480);
assert.strictEqual(entryTop.y, 1079);

console.log('✓ Layout & edge transition unit tests PASSED successfully.');
