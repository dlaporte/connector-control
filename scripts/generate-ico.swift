// Generates ConnectorControl.ico — the same artwork as the Mac AppIcon.icns
// (scripts/generate-icon.swift: SF Symbol "powerplug.fill" in white on a
// blue gradient rounded square) — as a Windows .ico with 32-bit BMP frames at
// 16, 24, 32, 48, 64 and 128 px and a PNG frame at 256 px.
//
// Usage: swift scripts/generate-ico.swift windows/assets/ConnectorControl.ico
// Run on macOS (needs AppKit for the SF Symbol). Commit the result.

import AppKit
import Foundation

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write("Usage: swift generate-ico.swift <output.ico>\n".data(using: .utf8)!)
    exit(1)
}
let outputPath = CommandLine.arguments[1]

let canvas: CGFloat = 1024
let inset: CGFloat = 100 // on the 1024 canvas, as in generate-icon.swift

func fail(_ message: String) -> Never {
    FileHandle.standardError.write((message + "\n").data(using: .utf8)!)
    exit(1)
}

func makeGlyphImage(pointSize: CGFloat) -> NSImage {
    guard let symbol = NSImage(systemSymbolName: "powerplug.fill", accessibilityDescription: nil) else {
        fail("Failed to load SF Symbol powerplug.fill")
    }
    let config = NSImage.SymbolConfiguration(pointSize: pointSize, weight: .medium)
    guard let configured = symbol.withSymbolConfiguration(config) else {
        fail("Failed to configure SF Symbol")
    }
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
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
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
    NSBezierPath(roundedRect: rect, xRadius: cornerRadius, yRadius: cornerRadius).addClip()
    let gradient = NSGradient(
        starting: NSColor(red: 0x2A / 255.0, green: 0x38 / 255.0, blue: 0x99 / 255.0, alpha: 1.0),
        ending: NSColor(red: 0x4A / 255.0, green: 0x70 / 255.0, blue: 0xFA / 255.0, alpha: 1.0)
    )
    gradient?.draw(in: rect, angle: 90)

    let targetGlyphWidth = rectSize * 0.55
    let glyph = makeGlyphImage(pointSize: 400)
    let glyphSize = glyph.size
    let scaleFactor = targetGlyphWidth / max(glyphSize.width, glyphSize.height)
    let drawSize = NSSize(width: glyphSize.width * scaleFactor, height: glyphSize.height * scaleFactor)
    let drawOrigin = NSPoint(x: rect.midX - drawSize.width / 2, y: rect.midY - drawSize.height / 2)
    glyph.draw(in: NSRect(origin: drawOrigin, size: drawSize), from: NSRect(origin: .zero, size: glyphSize),
               operation: .sourceOver, fraction: 1.0)

    NSGraphicsContext.current = previous
    return rep
}

/// Straight (non-premultiplied) RGBA bytes, top row first.
func rgba(_ rep: NSBitmapImageRep) -> [UInt8] {
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

func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)] }
func le32(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF), UInt8((v >> 16) & 0xFF), UInt8((v >> 24) & 0xFF)] }

/// A 32-bpp BI_RGB icon frame: BITMAPINFOHEADER (height doubled), bottom-up BGRA rows, then a 1-bpp AND mask.
func bmpFrame(_ rep: NSBitmapImageRep) -> [UInt8] {
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

func pngFrame(_ rep: NSBitmapImageRep) -> [UInt8] {
    guard let png = rep.representation(using: .png, properties: [:]) else { fail("Failed to encode PNG") }
    return [UInt8](png)
}

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
