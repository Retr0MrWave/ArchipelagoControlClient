// Decompile the functions containing the given addresses, to a file.
//
//   analyzeHeadless <proj> control -process Game -noanalysis \
//       -scriptPath <dir> -postScript decomp.java <out> <addr>...
//
// Java rather than Python because PyGhidra wants a pip install and this wants nothing.
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

import java.io.PrintWriter;

public class decomp extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("usage: decomp.java <out> <addr>...");
            return;
        }

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        try (PrintWriter out = new PrintWriter(args[0])) {
            for (int i = 1; i < args.length; i++) {
                Address addr = currentProgram.getAddressFactory().getAddress(args[i]);
                Function fn = getFunctionContaining(addr);

                out.println();
                out.println("==============================================================");
                if (fn == null) {
                    out.println("no function containing " + args[i]);
                    continue;
                }
                out.printf("%s  contains %s  (entry %s, %d bytes)%n",
                        fn.getName(), args[i], fn.getEntryPoint(),
                        fn.getBody().getNumAddresses());
                out.println("==============================================================");

                DecompileResults res = decomp.decompileFunction(fn, 120, monitor);
                if (res.decompileCompleted()) {
                    out.println(res.getDecompiledFunction().getC());
                } else {
                    out.println("decompilation failed: " + res.getErrorMessage());
                }
            }
        }
        println("wrote " + args[0]);
    }
}
