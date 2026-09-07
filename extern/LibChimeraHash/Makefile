CC = gcc

CFLAGS = -Wall -Wextra -O3 -flto -fvisibility=internal -fPIC -Icommon
LFLAGS = -s -shared

SRCS = $(wildcard common/*.c) $(wildcard crc32/*.c) $(wildcard sha1/*.c) bizinterface.c

ifeq ($(OS),Windows_NT)
EXT = dll
else
EXT = so
endif

all: libchimerahash

libchimerahash: $(SRCS)
	$(CC) $(CFLAGS) $(SRCS) -o ../../build/dll/libchimerahash.$(EXT) $(LFLAGS)
