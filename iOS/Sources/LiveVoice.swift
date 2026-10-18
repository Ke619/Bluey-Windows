import AVFoundation
import Foundation

/// Push-to-talk voice for the Windows version of Bluey.
/// Double tap wakes him. Hold the screen to talk; when you let go, the recording goes to the PC,
/// which asks Gemini (with the screen and the tools) and shows his reply as a speech bubble.
/// The phone only records, plays a little cartoon chirp, and shows the face.
final class LiveVoice: NSObject, ObservableObject {
    enum State: Equatable { case asleep, waking, listening, asking, thinking, speaking }

    typealias Answer = (heard: String, reply: String, sleep: Bool)

    @Published private(set) var state: State = .asleep

    /// Sends a WAV recording to the PC. Calls back with nil if the PC didn't answer.
    var askPC: ((Data, @escaping (Answer?) -> Void) -> Void)?
    /// Kept so the rest of the app compiles unchanged (the PC shows the bubble itself).
    var onCaption: ((String, _ done: Bool) -> Void)?
    var onStateChange: ((State) -> Void)?
    var onSessionStart: (() -> Void)?
    var onSessionEnd: (() -> Void)?
    var onUserTurn: ((_ itemID: String, _ asked: Bool) -> Void)?
    var onUserWords: ((_ itemID: String, _ text: String, _ asked: Bool) -> Void)?
    var onReply: ((String) -> Void)?
    var resumeContext: (() -> String?)?

    /// Not used in push-to-talk mode (no all-day recording), but the app still references it.
    let recorder = ChunkRecorder()

    private(set) var level: Double = 0

    var volume: Double {
        get { UserDefaults.standard.object(forKey: "volume") as? Double ?? 1 }
        set {
            UserDefaults.standard.set(newValue, forKey: "volume")
            player.volume = Float(newValue)
        }
    }

    private let engine = AVAudioEngine()
    private let player = AVAudioPlayerNode()
    private let playFormat = AVAudioFormat(standardFormatWithSampleRate: 24000, channels: 1)!
    private let wireFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: 16000, channels: 1, interleaved: true)!
    private var audioReady = false
    private var converter: AVAudioConverter?

    private var pcm = Data()
    private var holding = false
    private var capturing = false
    private var pendingBuffers = 0
    private var sleepAfterReply = false
    private var busy = false

    private func setState(_ new: State) {
        guard new != state else { return }
        state = new
        onStateChange?(new)
    }

    // MARK: Wake and sleep

    func toggle() {
        state == .asleep ? wake() : sleep()
    }

    func wake() {
        guard state == .asleep else { return }
        setState(.waking)
        prepare { [weak self] ok in
            guard let self else { return }
            guard ok else { self.setState(.asleep); return }
            self.onSessionStart?()
            self.chirp(syllables: 2)
            self.setState(self.holding ? .asking : .listening)
            if self.holding { self.startCapture() }
        }
    }

    func sleep() {
        let wasAwake = state != .asleep
        holding = false
        busy = false
        sleepAfterReply = false
        stopCapture()
        if wasAwake { onSessionEnd?() }
        player.stop()
        if engine.isRunning { engine.stop() }
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        #endif
        audioReady = false
        pendingBuffers = 0
        level = 0
        setState(.asleep)
    }

    /// A quick hi (for checking the chirp volume).
    func sayHi() {
        if state == .asleep { wake(); return }
        chirp(syllables: 3)
    }

    // MARK: Hold to ask

    func beginAsk() {
        holding = true
        onCaption?("", false)
        if state == .asleep { wake(); return }
        guard state == .listening || state == .speaking, !busy else { return }
        setState(.asking)
        startCapture()
    }

    func endAsk() {
        guard holding else { return }
        holding = false
        guard state == .asking else { return }
        let samples = stopCapture()
        // Under about a third of a second is an accidental press.
        guard samples.count > 16000 * 2 / 3 else { setState(.listening); return }
        sendQuestion(samples)
    }

    private func sendQuestion(_ samples: Data) {
        guard let askPC else { setState(.listening); return }
        busy = true
        setState(.thinking)
        let item = UUID().uuidString
        onUserTurn?(item, true)
        askPC(Self.wav(from: samples)) { [weak self] answer in
            DispatchQueue.main.async {
                guard let self, self.state != .asleep else { return }
                self.busy = false
                guard let answer else {
                    self.setState(.listening)  // the PC didn't answer; he just goes back to listening
                    return
                }
                if !answer.heard.isEmpty { self.onUserWords?(item, answer.heard, true) }
                if !answer.reply.isEmpty {
                    self.onReply?(answer.reply)
                    self.sleepAfterReply = answer.sleep
                    self.setState(.speaking)
                    self.chirp(syllables: min(max(answer.reply.split(separator: " ").count, 3), 5))
                } else if answer.sleep {
                    self.sleep()
                } else {
                    self.setState(.listening)
                }
            }
        }
    }

    // MARK: Audio

    private func prepare(_ done: @escaping (Bool) -> Void) {
        AVAudioApplication.requestRecordPermission { granted in
            DispatchQueue.main.async {
                guard granted else {
                    self.onCaption?("I need the microphone. Turn it on for Bluey in the iPhone's Settings.", true)
                    done(false)
                    return
                }
                do {
                    try self.startEngine()
                    done(true)
                } catch {
                    NSLog("Bluey audio error: \(error)")
                    done(false)
                }
            }
        }
    }

    private func startEngine() throws {
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playAndRecord, mode: .default, options: [.defaultToSpeaker, .allowBluetoothHFP])
        try session.setActive(true)
        #endif
        if !audioReady {
            engine.attach(player)
            engine.connect(player, to: engine.mainMixerNode, format: playFormat)
            player.installTap(onBus: 0, bufferSize: 1024, format: playFormat) { [weak self] buffer, _ in
                guard let self, let samples = buffer.floatChannelData?[0] else { return }
                var sum: Float = 0
                for i in 0..<Int(buffer.frameLength) { sum += samples[i] * samples[i] }
                let rms = sqrt(sum / Float(max(buffer.frameLength, 1)))
                self.level = min(1, Double(rms) * 5)
            }
            audioReady = true
        }
        player.volume = Float(volume)
        engine.prepare()
        try engine.start()
        player.play()
    }

    private func startCapture() {
        guard !capturing, engine.isRunning else { return }
        pcm = Data()
        let input = engine.inputNode
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0 else { return }
        converter = AVAudioConverter(from: inputFormat, to: wireFormat)
        input.removeTap(onBus: 0)
        input.installTap(onBus: 0, bufferSize: 2048, format: inputFormat) { [weak self] buffer, _ in
            self?.consume(buffer)
        }
        capturing = true
    }

    @discardableResult
    private func stopCapture() -> Data {
        if capturing {
            engine.inputNode.removeTap(onBus: 0)
            capturing = false
        }
        let data = pcm
        pcm = Data()
        return data
    }

    private func consume(_ buffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let ratio = wireFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio + 32)
        guard let out = AVAudioPCMBuffer(pcmFormat: wireFormat, frameCapacity: capacity) else { return }
        var fed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if fed { status.pointee = .noDataNow; return nil }
            fed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, out.frameLength > 0, let samples = out.int16ChannelData?[0] else { return }
        let chunk = Data(bytes: samples, count: Int(out.frameLength) * 2)
        DispatchQueue.main.async {
            guard self.capturing, self.pcm.count < 16000 * 2 * 90 else { return }  // 90 seconds at most
            self.pcm.append(chunk)
        }
    }

    /// Wraps raw 16 kHz mono 16-bit samples in a WAV header.
    private static func wav(from pcm: Data) -> Data {
        var header = Data()
        func u32(_ v: UInt32) { var x = v.littleEndian; header.append(Data(bytes: &x, count: 4)) }
        func u16(_ v: UInt16) { var x = v.littleEndian; header.append(Data(bytes: &x, count: 2)) }
        header.append("RIFF".data(using: .ascii)!)
        u32(UInt32(36 + pcm.count))
        header.append("WAVEfmt ".data(using: .ascii)!)
        u32(16); u16(1); u16(1); u32(16000); u32(32000); u16(2); u16(16)
        header.append("data".data(using: .ascii)!)
        u32(UInt32(pcm.count))
        return header + pcm
    }

    private func finishIfQuiet() {
        guard pendingBuffers == 0 else { return }
        if sleepAfterReply { sleep(); return }
        if state == .speaking { setState(holding ? .asking : .listening) }
    }

    /// A tiny cartoon chirp: a few soft, round, bell-like notes from a happy pentatonic scale.
    private func chirp(syllables: Int) {
        guard audioReady else { return }
        let rate = playFormat.sampleRate
        let scale = [1046.5, 1174.7, 1318.5, 1568.0, 1760.0, 2093.0, 2349.3, 2637.0]
        var index = Int.random(in: 1...3)
        var samples: [Float] = []
        for i in 0..<syllables {
            if i > 0 { index = min(max(index + [-1, 1, 1, 2].randomElement()!, 0), scale.count - 1) }
            let note = scale[index] * 0.5
            let duration = i == syllables - 1 ? 0.13 : Double.random(in: 0.07...0.09)
            let count = Int(duration * rate)
            var phase = 0.0
            for n in 0..<count {
                let time = Double(n) / rate
                let t = Double(n) / Double(count)
                let scoop = 1 - 0.18 * exp(-time / 0.012)
                let wobble = i == syllables - 1 ? 1 + 0.012 * sin(time * 2 * .pi * 18) : 1
                phase += 2 * .pi * note * scoop * wobble / rate
                let attack = min(1, time / 0.006)
                let envelope = attack * exp(-t * 3.2) * (1 - pow(t, 6))
                let wave = sin(phase) + 0.12 * sin(2 * phase)
                samples.append(Float(wave * envelope * 0.26))
            }
            samples += [Float](repeating: 0, count: Int(rate * 0.028))
        }
        guard let buffer = AVAudioPCMBuffer(pcmFormat: playFormat, frameCapacity: AVAudioFrameCount(samples.count)),
              let out = buffer.floatChannelData?[0] else { return }
        buffer.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { out.update(from: $0.baseAddress!, count: samples.count) }
        pendingBuffers += 1
        player.scheduleBuffer(buffer) { [weak self] in
            DispatchQueue.main.async {
                guard let self else { return }
                self.pendingBuffers = max(0, self.pendingBuffers - 1)
                if self.pendingBuffers == 0 { self.level = 0 }
                self.finishIfQuiet()
            }
        }
    }
}
