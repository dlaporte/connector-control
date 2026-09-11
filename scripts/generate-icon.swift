// Generates Connector Control's app icon — SF Symbol "powerplug.fill" in white on a blue
// gradient rounded square — as either a macOS .icns or a Windows .ico, chosen by the extension
// of the output path.
//
// Usage: swift scripts/generate-icon.swift <output.icns|output.ico>
// The .ico path is run by hand, on a Mac (AppKit is needed for the SF Symbol); commit its
// result — it's windows/assets/ConnectorControl.ico, the Windows build's app icon.

import AppKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

guard CommandLine.arguments.count == 2 else {
    fail("Usage: swift generate-icon.swift <output.icns|output.ico>")
}
let outputPath = CommandLine.arguments[1]
let outputExtension = (outputPath as NSString).pathExtension.lowercased()
guard outputExtension == "icns" || outputExtension == "ico" else {
    fail("Unsupported extension \".\(outputExtension)\": expected .icns or .ico")
}

let canvas: CGFloat = 1024
let inset: CGFloat = 100 // on the 1024 canvas

func makeGlyphImage(pointSize: CGFloat) -> NSImage {
    guard let symbol = NSImage(systemSymbolName: "powerplug.fill", accessibilityDescription: nil) else {
        fail("Failed to load SF Symbol powerplug.fill")
    }
    let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: .medium)
    guard let configured = symbol.withSymbolConfiguration(config) else {
        fail("Failed to configure SF Symbol")
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

/// Renders the icon at `pixels` × `pixels` into an RGBA bitmap.
func render(pixels: Int) -> NSBitmapImageRep {
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
    ) else { fail("Failed to create NSBitmapImageRep") }
    guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { fail("Failed to create NSGraphicsContext") }
    let previous = NSGraphicsContext.current
    NSGraphicsContext.current = ctx
    ctx.imageInterpolation = .high

    let scale = CGFloat(pixels) / canvas
    let px = inset * scale
    let rectSize = CGFloat(pixels) - 2 * px
    let rect = NSRect(x: px, y: px, width: rectSize, height: rectSize)
    let cornerRadius = 0.2237 * rectSize

    // Transparent background is implicit (fresh bitmap is zeroed).

    NSBezierPath(roundedRect: rect, xRadius: cornerRadius, yRadius: cornerRadius).addClip()

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

/// Writes a macOS .icns by rasterizing every size Apple's iconset format expects, then
/// shelling out to iconutil (the only supported way to assemble the format).
func writeIcns(to outputPath: String) {
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

    let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon-\(UUID().uuidString)")
    let iconset = tempDir.appendingPathComponent("AppIcon.iconset")

    do {
        try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
    } catch {
        fail("Failed to create iconset dir: \(error)")
    }

    var renderedCache: [Int: Data] = [:]
    for (pixels, filename) in iconSpecs {
        let pngData: Data
        if let cached = renderedCache[pixels] {
            pngData = cached
        } else {
            guard let data = render(pixels: pixels).representation(using: .png, properties: [:]) else {
                fail("Failed to encode PNG for size \(pixels)")
            }
            renderedCache[pixels] = data
            pngData = data
        }
        do {
            try pngData.write(to: iconset.appendingPathComponent(filename))
        } catch {
            fail("Failed to write \(filename): \(error)")
        }
    }

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
    process.arguments = ["-c", "icns", iconset.path, "-o", outputPath]
    do {
        try process.run()
        process.waitUntilExit()
    } catch {
        fail("Failed to run iconutil: \(error)")
    }
    guard process.terminationStatus == 0 else {
        fail("iconutil exited with status \(process.terminationStatus)")
    }

    try? FileManager.default.removeItem(at: tempDir)
    print("Wrote icon to \(outputPath)")
}

/// Straight (non-premultiplied) RGBA bytes, top row first.
private func rgba(_ rep: NSBitmapImageRep) -> [UInt8] {
    let w = rep.pixelsWide, h = rep.pixelsHigh
    var out = [UInt8](repeating: 0, count: w * h * 4)
    for y in 0..<h {
        for x in 0..<w {
            let c = rep.colorAt(x: x, y: y)!   // colorAt un-premultiplies for us
            let i = (y * w + x) * 4
            out[i] = UInt8(clamping: Int((c.redComponent * 255).rounded()))
            out[i + 1] = UInt8(clamping: Int((c.greenComponent * 255).rounded()))
            out[i + 2] = UInt8(clamping: Int((c.blueComponent * 255).rounded()))
            out[i + 3] = UInt8(clamping: Int((c.alphaComponent * 255).rounded()))
        }
    }
    return out
}

private func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)] }
private func le32(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8((v >> 24) & 0xFF)] }

/// A 32-bpp BI_RGB icon frame: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then a 1-bpp AND mask.
private func bmpFrame(_ rep: NSBitmapImageRep) -> [UInt8] {
    let w = rep.pixelsWide, h = rep.pixelsHigh
    let px = rgba(rep)
    var data: [UInt8] = []
    data += le32(40) + le32(w) + le32(h * 2) + le16(1) + le16(32) + le32(0)
    data += le32(w * h * 4) + le32(0) + le32(0) + le32(0) + le32(0)
    for y in stride(from: h - 1, through: 0, by: -1) {
        for x in 0..<w {
            let i = (y * w + x) * 4
            data += [px[i + 2], px[i + 1], px[i], px[i + 3]]   // BGRA
        }
    }
    let maskRowBytes = ((w + 31) / 32) * 4
    for y in stride(from: h - 1, through: 0, by: -1) {
        var row = [UInt8](repeating: 0, count: maskRowBytes)
        for x in 0..<w where px[(y * w + x) * 4 + 3] == 0 {
            row[x / 8] |= UInt8(0x80 >> (x % 8))   // 1 = transparent
        }
        data += row
    }
    return data
}

private func pngFrame(_ rep: NSBitmapImageRep) -> [UInt8] {
    guard let png = rep.representation(using: .png, properties: [:]) else { fail("Failed to encode PNG") }
    return [UInt8](png)
}

/// Writes a Windows .ico: BMP frames for every size but the largest, a PNG frame (the only
/// format ico readers accept above 256px) for that one.
func writeIco(to outputPath: String) {
    let sizes = [16, 24, 32, 48, 64, 128, 256]
    var frames: [(size: Int, bytes: [UInt8])] = []
    for size in sizes {
        let rep = render(pixels: size)
        frames.append((size, size == 256 ? pngFrame(rep) : bmpFrame(rep)))
    }

    var ico: [UInt8] = le16(0) + le16(1) + le16(frames.count)
    var offset = 6 + 16 * frames.count
    for frame in frames {
        let dim = frame.size == 256 ? 0 : frame.size
        ico += [UInt8(dim), UInt8(dim), 0, 0] + le16(1) + le16(32) + le32(frame.bytes.count) + le32(offset)
        offset += frame.bytes.count
    }
    for frame in frames {
        ico += frame.bytes
    }

    do {
        try Data(ico).write(to: URL(fileURLWithPath: outputPath))
    } catch {
        fail("Failed to write \(outputPath): \(error)")
    }
    print("Wrote \(outputPath) (\(ico.count) bytes, frames: \(sizes.map(String.init).joined(separator: ", ")))")
}

if outputExtension == "icns" {
    writeIcns(to: outputPath)
} else {
    writeIco(to: outputPath)
}
