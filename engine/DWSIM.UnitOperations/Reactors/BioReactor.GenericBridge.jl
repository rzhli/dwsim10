# Transport for the versioned BioReactor Julia interface. Standard library only.
# Model state definitions, units, integration and stream mappings belong to the model.
using Base64
let
    function write_json(io, x)
        if x === nothing
            print(io, "null")
        elseif x isa Bool
            print(io, x ? "true" : "false")
        elseif x isa Real
            value = Float64(x)
            isfinite(value) || error("BioReactor result contains a number outside the finite Float64 range")
            print(io, value)
        elseif x isa AbstractString || x isa Symbol
            print(io, '"')
            for c in string(x)
                if c == '"'; print(io, "\\\"")
                elseif c == '\\'; print(io, "\\\\")
                elseif Int(c) < 0x20; print(io, "\\u", string(Int(c); base=16, pad=4))
                else; print(io, c)
                end
            end
            print(io, '"')
        elseif x isa AbstractDict || x isa NamedTuple
            print(io, '{')
            first = true
            for (k, v) in pairs(x)
                first || print(io, ',')
                first = false
                write_json(io, string(k)); print(io, ':'); write_json(io, v)
            end
            print(io, '}')
        elseif x isa AbstractVector || x isa Tuple
            print(io, '[')
            for (i,v) in enumerate(x)
                i == 1 || print(io, ',')
                write_json(io,v)
            end
            print(io, ']')
        else
            error("Unsupported BioReactor result value: $(typeof(x)). Return dictionaries, named tuples, vectors and scalar values.")
        end
    end
    if !isdefined(Main, :_dwsim_generic_bioreactor_models)
        global _dwsim_generic_bioreactor_models = Dict{String,Module}()
    end
    model_key = @DWSIM_MODEL_HASH@
    if !haskey(_dwsim_generic_bioreactor_models, model_key)
        model = Module(gensym(:BioReactorModel))
        Base.include_string(model, @DWSIM_MODEL_SOURCE@, @DWSIM_SOURCE_FILENAME@)
        isdefined(model, :run_bioreactor) || error("A generic Julia model must define run_bioreactor(context).")
        _dwsim_generic_bioreactor_models[model_key] = model
    end
    model = _dwsim_generic_bioreactor_models[model_key]
    entry = Base.invokelatest(getfield, model, :run_bioreactor)
    context = @DWSIM_CONTEXT@
    Base.invokelatest(applicable, entry, context) || error("The selected Julia model does not support run_bioreactor(context).")
    result = Base.invokelatest(entry, context)
    (result isa AbstractDict || result isa NamedTuple) || error("run_bioreactor(context) must return a result dictionary or named tuple.")
    open(@DWSIM_OUTPUT_PATH@, "w") do io
        write_json(io, result)
    end
end
