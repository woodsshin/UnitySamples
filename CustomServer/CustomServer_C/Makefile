CXX      ?= g++
CXXFLAGS ?= -std=c++17 -O2 -Wall -Wextra -pthread
SRC_DIR  := src
BIN      := custom_server

SOURCES  := $(SRC_DIR)/main.cpp $(SRC_DIR)/CustomServer.cpp
HEADERS  := $(SRC_DIR)/CustomServer.h $(SRC_DIR)/Protocol.h $(SRC_DIR)/BinaryStream.h

.PHONY: all clean run

all: $(BIN)

$(BIN): $(SOURCES) $(HEADERS)
	$(CXX) $(CXXFLAGS) $(SOURCES) -o $(BIN)

run: $(BIN)
	./$(BIN)

clean:
	rm -f $(BIN)
