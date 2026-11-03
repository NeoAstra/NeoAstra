package com.neoastra.backend;

import java.util.Arrays;

/** Checks NeoAstraCaptureRegion on a plain JVM; AndroidJavaTests compiles and runs it, and a failure exits with 1. */
public final class NeoAstraCaptureRegionCheck {
    public static void main(String[] arguments) {
        // Without a rectangle the whole view, and nothing of a view without size.
        expect(new int[] {0, 0, 1080, 2205}, NeoAstraCaptureRegion.of(1080, 2205, 2.625f, 0, 0, 0, 0), "whole view");
        expect(null, NeoAstraCaptureRegion.of(0, 2205, 2.625f, 0, 0, 0, 0), "a view without width");
        // A rectangle in CSS pixels times the density: a card of 379 by 98 at 16, 500 on a display of 2.625.
        expect(new int[] {42, 1313, 1037, 1570}, NeoAstraCaptureRegion.of(1080, 2205, 2.625f, 16, 500, 379, 98), "a card");
        // A zoomed page doubles the pixels of a CSS pixel.
        expect(new int[] {84, 2100, 1080, 2205}, NeoAstraCaptureRegion.of(1080, 2205, 5.25f, 16, 400, 379, 98), "a zoomed page, cut to the view");
        // What lies partly outside is cut; what lies wholly outside is nothing.
        expect(new int[] {0, 0, 263, 263}, NeoAstraCaptureRegion.of(1080, 2205, 2.625f, -50, -50, 150, 150), "cut at the top left");
        expect(null, NeoAstraCaptureRegion.of(1080, 2205, 2.625f, 0, 900, 100, 100), "below the view");
        System.out.println("NeoAstraCaptureRegion: ok");
    }

    private static void expect(int[] expected, int[] actual, String message) {
        if (!Arrays.equals(expected, actual)) {
            System.out.println("FAILED: " + message + ": expected " + Arrays.toString(expected) + ", got " + Arrays.toString(actual));
            System.exit(1);
        }
    }
}
