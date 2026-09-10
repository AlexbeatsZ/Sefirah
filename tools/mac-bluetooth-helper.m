#import <Foundation/Foundation.h>
#import <IOBluetooth/IOBluetooth.h>

static void print_usage(void) {
    fprintf(stderr, "usage: sefirah-bluetooth --connect|--disconnect ADDRESS\n");
}

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc != 3) {
            print_usage();
            return 2;
        }

        NSString *action = [NSString stringWithUTF8String:argv[1]];
        NSString *address = [NSString stringWithUTF8String:argv[2]];
        BOOL connect;
        if ([action isEqualToString:@"--connect"]) {
            connect = YES;
        } else if ([action isEqualToString:@"--disconnect"]) {
            connect = NO;
        } else {
            print_usage();
            return 2;
        }

        IOBluetoothDevice *device = [IOBluetoothDevice deviceWithAddressString:address];
        if (device == nil) {
            fprintf(stderr, "Bluetooth device not found: %s\n", argv[2]);
            return 3;
        }

        if (device.isConnected == connect) {
            return 0;
        }

        IOReturn status = connect ? [device openConnection] : [device closeConnection];
        if (status != kIOReturnSuccess) {
            fprintf(stderr, "IOBluetooth returned status %d\n", status);
            return 4;
        }

        return 0;
    }
}
