import Foundation
import Network
import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

/// Finds the Windows PC app on the local network with Bonjour and keeps a connection open.
final class MacLink: ObservableObject {
    @Published private(set) var connected = false
    @Published private(set) var macName: String?
    /// Every Mac running Bluey on this Wi-Fi, by its Bonjour name.
    @Published private(set) var macs: [String] = []
    /// The Mac the phone is linked to (or trying to link to).
    @Published private(set) var currentMac: String?
    /// The Mac picked in the speaker panel. Remembered, so the phone goes back to it on launch.
    @Published private(set) var preferredMac: String? = UserDefaults.standard.string(forKey: "preferredMac")
    /// Commands from the Mac, like "wake" and "sleep".
    var onCommand: ((String) -> Void)?
    private var waiting: [String: (Packet?) -> Void] = [:]

    var onFace: ((FaceState) -> Void)?

    private var browser: NWBrowser?
    private var link: LineConnection?
    private var retry: DispatchWorkItem?

    func start() {
        guard browser == nil else { return }
        let browser = NWBrowser(for: .bonjour(type: GooglyService.type, domain: nil), using: .googly)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            self.macs = results.compactMap(Self.serviceName).sorted()
            // Move over when the picked Mac shows up while linked to a different one.
            if let preferred = self.preferredMac, self.currentMac != preferred, self.macs.contains(preferred) {
                self.link?.cancel()
            }
            self.connectIfNeeded()
        }
        browser.stateUpdateHandler = { [weak self] state in
            if case .failed = state { self?.restart() }
        }
        browser.start(queue: .main)
        self.browser = browser
    }

    func stop() {
        retry?.cancel()
        browser?.cancel()
        browser = nil
        link?.cancel()
        link = nil
        connected = false
    }

    private func restart() {
        stop()
        scheduleRetry()
    }

    private func scheduleRetry() {
        retry?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            if self.browser == nil { self.start() } else { self.connectIfNeeded() }
        }
        retry = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5, execute: work)
    }

    /// Switches to another Mac and remembers the choice.
    func choose(_ name: String) {
        preferredMac = name
        UserDefaults.standard.set(name, forKey: "preferredMac")
        if currentMac != name { link?.cancel() }
        connectIfNeeded()
    }

    /// Connects straight to an address like "192.168.1.20" or "192.168.1.20:48731" (empty text goes back to automatic).
    func connectManually(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            UserDefaults.standard.removeObject(forKey: "manualHost")
        } else {
            UserDefaults.standard.set(trimmed, forKey: "manualHost")
        }
        if link != nil { link?.cancel() } else { connectIfNeeded() }
    }

    private static func manualEndpoint() -> NWEndpoint? {
        guard let text = UserDefaults.standard.string(forKey: "manualHost"), !text.isEmpty else { return nil }
        let parts = text.split(separator: ":", maxSplits: 1).map(String.init)
        guard let host = parts.first, !host.isEmpty else { return nil }
        let portNumber = parts.count > 1 ? UInt16(parts[1]) : 48731
        guard let portNumber, let port = NWEndpoint.Port(rawValue: portNumber) else { return nil }
        return .hostPort(host: NWEndpoint.Host(host), port: port)
    }

    private static func serviceName(_ result: NWBrowser.Result) -> String? {
        if case let .service(name, _, _, _) = result.endpoint { return name }
        return nil
    }

    private func connectIfNeeded() {
        guard link == nil else { return }
        let endpoint: NWEndpoint
        if let manual = Self.manualEndpoint() {
            // Typed-in address: skips Bonjour completely.
            endpoint = manual
            currentMac = UserDefaults.standard.string(forKey: "manualHost")
        } else if let results = browser?.browseResults, !results.isEmpty {
            let result = results.first { Self.serviceName($0) == preferredMac } ?? results.first!
            currentMac = Self.serviceName(result)
            endpoint = result.endpoint
        } else {
            return
        }
        let link = LineConnection(NWConnection(to: endpoint, using: .googly))
        link.onState = { [weak self, weak link] state in
            guard let self, let link, link === self.link else { return }
            switch state {
            case .ready:
                self.connected = true
                link.send(Packet(hello: Self.deviceName))
            case .failed, .cancelled:
                self.connected = false
                let waiting = self.waiting
                self.waiting = [:]
                waiting.values.forEach { $0(nil) }
                self.macName = nil
                self.link = nil
                self.scheduleRetry()
            case .waiting:
                link.cancel()
            default:
                break
            }
        }
        link.onPacket = { [weak self] packet in
            if let name = packet.hello { self?.macName = name }
            if let face = packet.face { self?.onFace?(face) }
            guard let self, let command = packet.command else { return }
            if let callID = packet.callID, let done = self.waiting.removeValue(forKey: callID) {
                done(packet)
            } else {
                self.onCommand?(command)
            }
        }
        self.link = link
        link.start()
    }

    func send(_ packet: Packet) {
        link?.send(packet)
    }

    /// Sends a request to the Mac and calls back with its reply (nil if the Mac went away).
    func request(_ packet: Packet, _ done: @escaping (Packet?) -> Void) {
        guard let link, connected else { done(nil); return }
        var packet = packet
        let id = UUID().uuidString
        packet.callID = id
        waiting[id] = done
        link.send(packet)
    }

    private static var deviceName: String {
        #if canImport(UIKit)
        return UIDevice.current.name
        #else
        return Host.current().localizedName ?? "Phone"
        #endif
    }
}
