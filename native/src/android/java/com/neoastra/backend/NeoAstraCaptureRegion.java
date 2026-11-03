package com.neoastra.backend;

/**
 * The pixels of a view that a capture takes: the whole view without a rectangle, else the rectangle in CSS pixels times the
 * scale of the page (the display density times its zoom), cut to the view. It has no Android type, so that a plain JVM tests it.
 */
final class NeoAstraCaptureRegion {
    private NeoAstraCaptureRegion() {
    }

    /** Returns left, top, right, and bottom in the pixels of the view, or null when nothing of the view is left. */
    static int[] of(int viewWidth, int viewHeight, float scale, int x, int y, int width, int height) {
        if (viewWidth <= 0 || viewHeight <= 0) return null;
        if (width <= 0 || height <= 0) return new int[] {0, 0, viewWidth, viewHeight};
        int left = Math.max(0, Math.round(x * scale)), top = Math.max(0, Math.round(y * scale));
        int right = Math.min(viewWidth, Math.round((x + width) * scale)), bottom = Math.min(viewHeight, Math.round((y + height) * scale));
        return left < right && top < bottom ? new int[] {left, top, right, bottom} : null;
    }
}
