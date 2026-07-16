public struct FormModel: Equatable {
    public var command: String
    public var args: [String]
    public var env: [String: String]
    /// Keys the form has no widget for — preserved verbatim, shown read-only.
    public var additional: [String: JSONValue]

    public init(command: String = "", args: [String] = [],
                env: [String: String] = [:], additional: [String: JSONValue] = [:]) {
        self.command = command
        self.args = args
        self.env = env
        self.additional = additional
    }
}

public struct FormAnalysis: Equatable {
    public var model: FormModel
    /// Human-readable descriptions of elements the form CANNOT represent.
    /// Empty means switching JSON → Form loses nothing.
    public var lost: [String]
    public var isLossless: Bool { lost.isEmpty }
}

public enum FormMapper {
    private static let formKeys: Set<String> = ["command", "args", "env"]

    public static func analyze(_ config: JSONValue) -> FormAnalysis {
        guard case .object(let object) = config else {
            return FormAnalysis(model: FormModel(),
                                lost: ["entire configuration (not a JSON object)"])
        }
        var model = FormModel()
        var lost: [String] = []

        switch object["command"] {
        case .string(let s): model.command = s
        case .none: break
        case .some(let other): lost.append("command (\(other.typeName))")
        }

        switch object["args"] {
        case .array(let items):
            for (index, item) in items.enumerated() {
                if case .string(let s) = item { model.args.append(s) }
                else { lost.append("args[\(index)] (\(item.typeName))") }
            }
        case .none: break
        case .some: lost.append("args (not an array)")
        }

        switch object["env"] {
        case .object(let pairs):
            for (key, value) in pairs {
                if case .string(let s) = value { model.env[key] = s }
                else { lost.append("env.\(key) (\(value.typeName))") }
            }
        case .none: break
        case .some: lost.append("env (not an object)")
        }

        model.additional = object.filter { !formKeys.contains($0.key) }
        return FormAnalysis(model: model, lost: lost.sorted())
    }

    public static func serialize(_ model: FormModel) -> JSONValue {
        var object = model.additional
        if !model.command.isEmpty {
            object["command"] = .string(model.command)
        }
        if !model.args.isEmpty {
            object["args"] = .array(model.args.map(JSONValue.string))
        }
        if !model.env.isEmpty {
            object["env"] = .object(model.env.mapValues(JSONValue.string))
        }
        return .object(object)
    }
}
