# Private host for the Script Manager's persistent, per-flowsheet Julia process.
# Requests contain a run ID and base64-encoded working directory, filename and source.
module DWSIMJuliaHost

using Base64

function main()
    token = ARGS[1]
    requests, output, errors = stdin, stdout, stderr

    for line in eachline(requests)
        fields = split(line, '\t'; limit=4, keepempty=true)
        run_id = fields[1]
        status = "ERROR"
        try
            directory = String(base64decode(fields[2]))
            filename = String(base64decode(fields[3]))
            source = String(base64decode(fields[4]))
            cd(directory) do
                # The request pipe is private; scripts cannot consume the next request.
                redirect_stdin(devnull) do
                    Base.include_string(Main, source, filename)
                end
            end
            status = "OK"
        catch err
            showerror(errors, err, catch_backtrace())
            println(errors)
        end

        # A marker on each pipe lets the host drain both streams before completing a run.
        flush(output)
        flush(errors)
        println(output, token, ":", run_id, ":", status)
        println(errors, token, ":", run_id, ":", status)
        flush(output)
        flush(errors)
    end
end

end

DWSIMJuliaHost.main()
