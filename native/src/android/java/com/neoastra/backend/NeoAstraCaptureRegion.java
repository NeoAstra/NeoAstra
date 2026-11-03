package com.neoastra.backend;

/**
 * The pixels of a view that a capture takes: the whole view without a rectangle, else the rectangle in CSS pixels times the
 * scale of the page (the display density times its zoom), cut to the view. It has no Android type, so that a plain JVM tests it.
 */
final class NeoAstraCaptureRegion {
    private NeoAstraCaptureRegion() {
    }

    /**
     * Returns left, top, right, and bottom in the pixels of the view, or null when nothing of the view is left. The far edge of
     * the rectangle and its scaling are counted wider than an int, so that a rectangle which starts in the view and reaches
     * past the end of the coordinates is cut to the view instead of wrapping around it.
     */
    static int[] of(int viewWidth, int viewHeight, float scale, int x, int y, int width, int height) {
        if (viewWidth <= 0 || viewHeight <= 0) return null;
        if (width <= 0 || height <= 0) return new int[] {0, 0, viewWidth, viewHeight};
        long left = Math.max(0, Math.round(x * (double) scale)), top = Math.max(0, Math.round(y * (double) scale));
        long right = Math.min(viewWidth, Math.round(((double) x + width) * scale)), bottom = Math.min(viewHeight, Math.round(((double) y + height) * scale));
        // Only a rectangle that keeps pixels is returned, and those lie between 0 and the size of the view: they are ints.
        return left < right && top < bottom ? new int[] {(int) left, (int) top, (int) right, (int) bottom} : null;
    }
}
