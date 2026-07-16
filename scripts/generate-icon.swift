// Generates AppIcon.icns rendering the SF Symbol "powerplug.fill" — the same
// glyph used in the menu bar item — as a standard macOS app icon.
//
// Usage: swift scripts/generate-icon.swift <output.icns>

import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write("Usage: swift generate-icon.swift <output.icns>\n".data(using: .utf8)!)
    exit(1)
}
let outputPath = CommandLine.arguments[1]

let canvas: CGFloat = 1024
let inset: CGFloat = 100 // on the 1024 canvas

// (pixel size, filename)
let iconSpecs: [(Int, String)] = [
    (16, "icon_16x16.png"),
    (32, "icon_16x16@2x.png"),
    (32, "icon_32x32.png"),
    (64, "icon_32x32@2x.png"),
    (128, "icon_128x128.png"),
    (256, "icon_128x128@2x.png"),
    (256, "icon_256x256.png"),
    (512, "icon_256x256@2x.png"),
    (512, "icon_512x512.png"),
    (1024, "icon_512x512@2x.png"),
]

func makeGlyphImage(pointSize: CGFloat) -> NSImage {
    guard let symbol = NSImage(systemSymbolName: "powerplug.fill", accessibilityDescription: nil) else {
        FileHandle.standardError.write("Failed to load SF Symbol powerplug.fill\n".data(using: .utf8)!)
        exit(1)
    }
    let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: .medium)
    guard let configured = symbol.withSymbolConfiguration(config) else {
        FileHandle.standardError.write("Failed to configure SF Symbol\n".data(using: .utf8)!)
        exit(1)
    }
    // Tint white: draw the (black) glyph into an offscreen image, then fill
    // its bounds with white using .sourceAtop so only the glyph's alpha survives.
    let size = configured.size
    let tinted = NSImage(size: size)
    tinted.lockFocus()
    configured.draw(at: .zero, from: .zero, operation: .sourceOver, fraction: 1.0)
    NSColor.white.set()
    NSRect(origin: .zero, size: size).fill(using: .sourceAtop)
    tinted.unlockFocus()
    return tinted
}

func renderMaster(pixels: Int) -> NSBitmapImageRep {
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: pixels,
        pixelsHigh: pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    ) else {
        FileHandle.standardError.write("Failed to create NSBitmapImageRep\n".data(using: .utf8)!)
        exit(1)
    }

    guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else {
        FileHandle.standardError.write("Failed to create NSGraphicsContext\n".data(using: .utf8)!)
        exit(1)
    }

    let previous = NSGraphicsContext.current
    NSGraphicsContext.current = ctx
    ctx.imageInterpolation = .high

    let scale = CGFloat(pixels) / canvas
    let px = inset * scale
    let rectSize = CGFloat(pixels) - 2 * px
    let rect = NSRect(x: px, y: px, width: rectSize, height: rectSize)
    let cornerRadius = 0.2237 * rectSize

    // Transparent background is implicit (fresh bitmap is zeroed).

    let path = NSBezierPath(roundedRect: rect, xRadius: cornerRadius, yRadius: cornerRadius)
    path.addClip()

    // NSGradient's angle: 90 draws the starting color at the bottom of the
    // rect and the ending color at the top (bitmap contexts are unflipped,
    // origin at bottom-left), so pass dark-as-starting/bright-as-ending to
    // get the desired bright-top / dark-bottom appearance.
    let gradient = NSGradient(
        starting: NSColor(red: 0x2A / 255.0, green: 0x38 / 255.0, blue: 0x99 / 255.0, alpha: 1.0),
        ending: NSColor(red: 0x4A / 255.0, green: 0x70 / 255.0, blue: 0xFA / 255.0, alpha: 1.0)
    )
    gradient?.draw(in: rect, angle: 90) // bottom (start, dark) to top (end, bright)

    // Draw the white glyph, centered, scaled to ~55% of the rounded rect's width.
    let targetGlyphWidth = rectSize * 0.55
    // Render glyph at a generous point size for crisp rasterization, then scale to fit.
    let glyph = makeGlyphImage(pointSize: 400)
    let glyphSize = glyph.size
    let scaleFactor = targetGlyphWidth / max(glyphSize.width, glyphSize.height)
    let drawSize = NSSize(width: glyphSize.width * scaleFactor, height: glyphSize.height * scaleFactor)
    let drawOrigin = NSPoint(
        x: rect.midX - drawSize.width / 2,
        y: rect.midY - drawSize.height / 2
    )
    glyph.draw(
        in: NSRect(origin: drawOrigin, size: drawSize),
        from: NSRect(origin: .zero, size: glyphSize),
        operation: .sourceOver,
        fraction: 1.0
    )

    NSGraphicsContext.current = previous
    return rep
}

let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon-\(UUID().uuidString)")
let iconset = tempDir.appendingPathComponent("AppIcon.iconset")

do {
    try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
} catch {
    FileHandle.standardError.write("Failed to create iconset dir: \(error)\n".data(using: .utf8)!)
    exit(1)
}

var renderedCache: [Int: Data] = [:]

for (pixels, filename) in iconSpecs {
    let pngData: Data
    if let cached = renderedCache[pixels] {
        pngData = cached
    } else {
        let rep = renderMaster(pixels: pixels)
        guard let data = rep.representation(using: .png, properties: [:]) else {
            FileHandle.standardError.write("Failed to encode PNG for size \(pixels)\n".data(using: .utf8)!)
            exit(1)
        }
        renderedCache[pixels] = data
        pngData = data
    }
    let fileURL = iconset.appendingPathComponent(filename)
    do {
        try pngData.write(to: fileURL)
    } catch {
        FileHandle.standardError.write("Failed to write \(filename): \(error)\n".data(using: .utf8)!)
        exit(1)
    }
}

let process = Process()
process.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
process.arguments = ["-c", "icns", iconset.path, "-o", outputPath]

do {
    try process.run()
    process.waitUntilExit()
} catch {
    FileHandle.standardError.write("Failed to run iconutil: \(error)\n".data(using: .utf8)!)
    exit(1)
}

guard process.terminationStatus == 0 else {
    FileHandle.standardError.write("iconutil exited with status \(process.terminationStatus)\n".data(using: .utf8)!)
    exit(1)
}

try? FileManager.default.removeItem(at: tempDir)

print("Wrote icon to \(outputPath)")
