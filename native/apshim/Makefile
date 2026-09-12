# libapcontrol.dylib - the in-process helper the Ap.Control client drives.
#
# It links against nothing from the game. Every engine symbol it touches is left undefined and
# resolved by dyld at load time, so the shim records no dependency on the game's dylibs, needs no
# rpath pointing into the install, and does not have to be rebuilt when the game updates.
#
#   make          build out/libapcontrol.dylib
#   make check    prove the interposers fire, without needing Control installed
#   make install  copy the dylib and launch wrapper where the client expects them
#   make clean

SHELL    := /bin/sh
ARCH     ?= arm64
MIN_OS   ?= 14.0          # Game.app's LSMinimumSystemVersion
OUT      ?= out

DYLIB    := $(OUT)/libapcontrol.dylib
SRCS     := $(wildcard src/*.cpp)
OBJS     := $(SRCS:src/%.cpp=$(OUT)/obj/%.o)
DEPS     := $(OBJS:.o=.d)

CXX      ?= clang++
COMMON   := -arch $(ARCH) -mmacosx-version-min=$(MIN_OS) -std=c++17
CXXFLAGS += $(COMMON) -O2 -g -fvisibility=hidden -Wall -Wextra -MMD -MP
LDFLAGS  += $(COMMON) -dynamiclib -install_name libapcontrol.dylib \
            -Wl,-undefined,dynamic_lookup

INSTALL_DIR ?= $(HOME)/Library/Application Support/Ap.Control

.PHONY: all clean check install
all: $(DYLIB)

$(DYLIB): $(OBJS) | $(OUT)
	$(CXX) $(LDFLAGS) -o $@ $(OBJS)
# arm64 refuses to load an unsigned image. Ad-hoc is enough: the game is entitled with
# com.apple.security.cs.disable-library-validation, so it does not require our signer to match its
# own. Signing here rather than relying on the linker's ad-hoc signature keeps the step explicit.
	codesign --sign - --force --timestamp=none $@

$(OUT)/obj/%.o: src/%.cpp | $(OUT)/obj
	$(CXX) $(CXXFLAGS) -c $< -o $@

$(OUT) $(OUT)/obj:
	mkdir -p $@

# --- smoke test ------------------------------------------------------------------------------
#
# Stand-ins that export and call the very symbols the shim interposes, so the mechanism can be
# exercised on a machine with no game installed - and, more to the point, in CI.

FAKECORE := $(OUT)/libfakecore.dylib
FAKEGAME := $(OUT)/fakegame

$(FAKECORE): test/fakecore.cpp | $(OUT)
	$(CXX) $(COMMON) -O0 -g -dynamiclib -install_name @rpath/libfakecore.dylib -o $@ $<

$(FAKEGAME): test/fakegame.cpp $(FAKECORE)
	$(CXX) $(COMMON) -O0 -g -o $@ $< -L$(OUT) -lfakecore -Wl,-rpath,@executable_path

check: $(DYLIB) $(FAKEGAME)
	@./test/run-check.sh "$(OUT)"

# --- install ---------------------------------------------------------------------------------

install: $(DYLIB)
	mkdir -p "$(INSTALL_DIR)"
	cp $(DYLIB) "$(INSTALL_DIR)/"
	cp apcontrol-launch.sh "$(INSTALL_DIR)/"
	chmod 755 "$(INSTALL_DIR)/apcontrol-launch.sh"
# A dylib that still carries a quarantine flag is refused inside a hardened process, and the flag
# is attached by whatever downloaded it, not by us.
	xattr -d com.apple.quarantine "$(INSTALL_DIR)/libapcontrol.dylib" 2>/dev/null || true
	@echo
	@echo "Installed to $(INSTALL_DIR)"
	@echo "Steam -> Control -> Properties -> Launch Options:"
	@echo "  \"$(INSTALL_DIR)/apcontrol-launch.sh\" %command%"

clean:
	rm -rf $(OUT)

-include $(DEPS)
