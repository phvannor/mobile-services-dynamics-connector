//
//  AzureConnector.m
//  Azure Activity Logger
//

#import "AzureConnector.h"
#import "CoreDataHelper.h"
#import "ADAuthenticationContext.h"
#import "ADAuthenticationSettings.h"
#import "ADKeychainTokenCacheStore.h"
#import "SSKeychain.h"
#import "QSFilter.h"

// Configure the values based based on your Mobile Service settings

// endpoint for your hosted mobile service
NSString *const mobileServiceUrl =    @"INSERT MOBILE SERVICE URI";

// Application Key, retrieve from your Mobile Service in the Azure Management portal
NSString *const mobileServiceAppKey = @"INSERT MOBILE SERVICE APP KEY";

// Active Directory authority, usually of the form "https://login.windows.net/yourtenant/onmicrosoft.com"
NSString *const aadAuthorityUri =     @"INSERT AUTHORITY URI";

// client ID for native app registration in Azure Active Directory
NSString *const activeDirectoryNativeClientId = @"INSERT CLIENT ID";

/**
 * The following constants are meant for end users to use to determine the status
 * of the sync process. The key constants are for end users to determine the
 * overall success/failure of the sync process.
 */

/// Sync status notification names.
NSString *const AzureConnectorSyncStarted = @"AzureConnectorSyncStarted";
NSString *const AzureConnectorSyncCompleted = @"AzureConnectorSyncCompleted";

/// Sync success/failure notification user info keys.
NSString *const AzureConnectorSyncSuccessKey = @"AzureConnectorSyncSuccessKey";
NSString *const AzureConnectorSyncFailedMessagesKey = @"AzureConnectorSyncFailedMessagesKey";

/**
 * The following keys are private to this class and not intended for use elsewhere.
 * These keys setup the basic defaults the app will use. The app allows the user to
 * enter new values for these keys within the settings view.
 */
/// Private key for use with NSUserDefaults to store the last successful sync date.
NSString *const kAzureConnectorSyncCompletedDateKey = @"AzureConnectorSyncCompletedDateKey";

@interface AzureConnector ()

@property (nonatomic, strong) MSClient *client;
@property (nonatomic, strong) MSSyncTable *contactTable;
@property (nonatomic, strong) MSSyncTable *taskTable;
@property (nonatomic, strong) MSSyncTable *phoneCallTable;
@property (nonatomic, strong) MSSyncTable *appointmentTable;
@property (nonatomic, strong) NSArray *syncTables;

@end

@implementation AzureConnector

+ (AzureConnector *)sharedConnector {
    static AzureConnector *connector;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        connector = [[AzureConnector alloc] init];
    });
    return connector;
}

- (instancetype)init {
    self = [super init];
    if (self) {
        self.client = [self setupClient];
        self.syncTables = [self setupSyncTables];
        [self loadAuthInfo];
    }
    return self;
}

- (MSClient *)setupClient {
    MSClient *client = [MSClient clientWithApplicationURLString:mobileServiceUrl applicationKey:mobileServiceAppKey];
    QSFilter *filter = [[QSFilter alloc] init];
    client = [client clientWithFilter:filter];
    filter.client = client;
    MSCoreDataStore *dataStore = [[MSCoreDataStore alloc] initWithManagedObjectContext:[CoreDataHelper getContext]];
    client.syncContext = [[MSSyncContext alloc] initWithDelegate:nil dataSource:dataStore callback:nil];

    return client;
}

// This function returns the list of table names so that later, when syncing
// the necessary table can be created as needed.
- (NSArray *)setupSyncTables {
    self.contactTable = [self.client syncTableWithName:@"Contact"];
    self.taskTable = [self.client syncTableWithName:@"Task"];
    self.phoneCallTable = [self.client syncTableWithName:@"PhoneCall"];
    self.appointmentTable = [self.client syncTableWithName:@"Appointment"];
    
    return @[@"Contact", @"Task", @"PhoneCall", @"Appointment"];
}

- (NSDate *)lastSyncDate {
    NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
    return [defaults valueForKey:kAzureConnectorSyncCompletedDateKey];
}

- (void)setLastSyncDate:(NSDate *)date {
    dispatch_async(dispatch_get_main_queue(), ^{
        NSUserDefaults *defaults = [NSUserDefaults standardUserDefaults];
        [defaults setValue:date forKey:kAzureConnectorSyncCompletedDateKey];
        [defaults synchronize];
    });
}

- (NSUInteger)pendingSyncCount {
    return self.client.syncContext.pendingOperationsCount;
}

- (BOOL)isLoggedIn {
    return self.client.currentUser != nil;
}

- (void)saveAuthInfo {
    [SSKeychain setPassword:self.client.currentUser.mobileServiceAuthenticationToken forService:@"AzureMobileServiceTutorial" account:self.client.currentUser.userId];
}

- (void)loadAuthInfo {
    NSString *userid = [[SSKeychain accountsForService:@"AzureMobileServiceTutorial"][0] valueForKey:@"acct"];
    if (userid) {
        NSLog(@"userid: %@", userid);
        self.client.currentUser = [[MSUser alloc] initWithUserId:userid];
        self.client.currentUser.mobileServiceAuthenticationToken = [SSKeychain passwordForService:@"AzureMobileServiceTutorial" account:userid];
    }
}

- (void)clearAuthInfo {
    NSString *userId = [[SSKeychain accountsForService:@"AzureMobileServiceTutorial"][0] valueForKey:@"acct"];
    [SSKeychain deletePasswordForService:@"AzureMobileServiceTutorial" account:userId];
    self.client.currentUser.mobileServiceAuthenticationToken = nil;
    
    id<ADTokenCacheStoring> cache = [ADAuthenticationSettings sharedInstance].defaultTokenCacheStore;
    [cache removeAllWithError:nil];
    [ADAuthenticationSettings sharedInstance].defaultTokenCacheStore = [ADKeychainTokenCacheStore new];
    
    for(NSHTTPCookie *cookie in [[NSHTTPCookieStorage sharedHTTPCookieStorage] cookies]) {
        [[NSHTTPCookieStorage sharedHTTPCookieStorage] deleteCookie:cookie];
    }
    
    NSString *logoutUrl = @"https://login.windows.net/common/oauth2/logout";
    NSURLSession *session = [NSURLSession sharedSession];
    NSURLSessionDataTask *logoutTask = [session dataTaskWithURL:[NSURL URLWithString:logoutUrl] completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
        if (!error) {
            NSLog(@"Success logging out");
        } else {
            NSLog(@"Error logging out : %@", error);
        }
    }];
    [logoutTask resume];
}

- (ADAuthenticationContext *)authenticationContext {
    return [ADAuthenticationContext authenticationContextWithAuthority:aadAuthorityUri validateAuthority:NO error:nil];
}

- (void)loginWithController:(UIViewController *)controller completion:(MSClientLoginBlock)completion {
    if (!self.client) {
        self.client = [self setupClient];
        self.syncTables = [self setupSyncTables];
    }

    NSString *resourceURI = self.resourceURI;
    NSString *clientID = self.clientID;
    NSURL *redirectURI = [NSURL URLWithString:[self redirectURI]];

    // Use ADAL to authenticate AAD first
    ADAuthenticationContext *context = [self authenticationContext];
    if (!context) {
        NSLog(@"Error getting authentication context");
        return;
    }

    [context acquireTokenWithResource:resourceURI clientId:clientID redirectUri:redirectURI completionBlock:^(ADAuthenticationResult *result) {
        if (result.status != AD_SUCCEEDED) {
            NSLog(@"Error authenticating: %@\n%@", result.error.localizedDescription, result.error.localizedFailureReason);
            completion(nil, result.error);
            return;
        }

        // With a successful authentication to AAD, it is possible to now authenticate
        // against Mobile Services
        NSDictionary *tokenDict = @{ @"access_token" : result.accessToken };

        void (^finalCompletion)(MSUser *, NSError *) = ^void(MSUser *user, NSError *error) {
            if (!error) {
                [self saveAuthInfo];
            }

            completion(user, error);
        };

        [self.client loginWithProvider:@"aad" token:tokenDict completion:finalCompletion];
    }];
}

- (void)syncWithCompletion:(MSSyncBlock)completion {
    void (^finalCompletion)(NSError *) = ^void(NSError *error) {
        completion(error);

        if (!error) {
            [self setLastSyncDate:[NSDate date]];
        }

        NSMutableDictionary *userInfo = [NSMutableDictionary dictionary];
        userInfo[AzureConnectorSyncSuccessKey] = @(!error);
        if (error) {
            userInfo[AzureConnectorSyncFailedMessagesKey] = error.localizedDescription;
        }

        [[NSNotificationCenter defaultCenter] postNotificationName:AzureConnectorSyncCompleted object:nil userInfo:userInfo];
    };

    [[NSNotificationCenter defaultCenter] postNotificationName:AzureConnectorSyncStarted object:nil];

    [self syncTables:self.syncTables withTotalCompletion:finalCompletion];
}

// This method will recursively sync all tables that are in the `syncTables` property.
// The reason for a recursive design is so that there is less overhead in tracking which
// operations are started but not yet completed. Additionally it allows for short
// circuiting the execution if there is an error.
- (void)syncTables:(NSArray *)tables withTotalCompletion:(MSSyncBlock)completion {
    if (tables.count == 0) {
        completion(nil);
        return;
    }

    MSSyncTable *curTable = [self.client syncTableWithName:[tables firstObject]];
    MSQuery *curQuery = [curTable query];
    [curTable pullWithQuery:curQuery queryId:@"AllItems" completion:^(NSError *error) {
        if (error) {
            completion(error);
            return;
        }

        NSMutableArray *mutableTables = [NSMutableArray arrayWithArray:tables];
        [mutableTables removeObjectAtIndex:0];
        [self syncTables:[mutableTables copy] withTotalCompletion:completion];
    }];
}

- (void)logout {
    [self clearAuthInfo];

    // On logout there is the assumption that all data should be deleted. Here there
    // is no need for recursion as the only concern is that all data is removed, not
    // the order of execution or if one completes before another.
    for (NSString *tableName in self.syncTables) {
        MSSyncTable *table = [self.client syncTableWithName:tableName];
        [table forcePurgeWithCompletion:^(NSError *error) {
            if (error) {
                NSLog(@"Error purging table : %@", table);
            }
        }];
    }
    [self.client logout];
    self.client = nil;
    self.lastSyncDate = nil;
}

#pragma mark - Insert methods

// Wrap up the insert to a specific table. This allows the consumer to be independent
// of any work that goes in to inserting the object.
- (void)insertTask:(NSDictionary *)task completion:(MSSyncItemBlock)completion {
    [self.taskTable insert:task completion:completion];
}


#pragma mark - Settings getters

- (NSString *)resourceURI {
    return [mobileServiceUrl stringByAppendingString:@"/login/aad"];
}

- (NSString *)redirectURI {
    return [mobileServiceUrl stringByAppendingString:@"/login/done"];
}

- (NSString *)clientID {
    return activeDirectoryNativeClientId;
}

- (NSString *)applicationURL {
    return mobileServiceUrl;
}

@end

