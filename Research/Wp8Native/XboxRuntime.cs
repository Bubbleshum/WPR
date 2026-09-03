namespace WPR.Wp8Native
{
    /// <summary>
    /// The five <c>Microsoft.Xbox</c> interfaces this title binds, implemented against the
    /// metadata it ships rather than improvised.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Xbox.winmd</c> is in the XAP, so the vtable layouts here are read off the
    /// game's own package rather than guessed: declaration order is vtable order, and members
    /// start at slot 6 after IInspectable. Every slot below is annotated with the signature it
    /// implements so the two can be checked against each other.
    /// <para>
    /// This exists because a stand-in that answers S_OK to everything is what kept the image on
    /// its LOADING screen. Its Lua rests until the Xbox callbacks arrive - `restUntilCallback`,
    /// `hideLoadingInitXBOX` - and a stand-in promises they are coming without ever sending
    /// one.
    /// </para>
    /// <para>
    /// Everything here reports a signed-in player with an empty everything-else: no friends, no
    /// achievements, no leaderboards, no messages. That is a lie, but it is the *shape* of the
    /// truth - there is no Xbox Live here - and it is the shape the image is written to cope
    /// with, where "the call never came back" is not.
    /// </para>
    /// </remarks>
    public sealed partial class WinRtRuntime
    {
        /// <summary>The synthetic player. A gamertag is 15 characters at most on Xbox.</summary>
        private const string PlayerGamertag = "WPRPlayer";

        /// <summary>
        /// A plausible XUID. Real ones are 2533274790395904 upwards - the top bits are a
        /// namespace - and something in that range is likelier to survive a range check than
        /// zero, which a game may read as "no user".
        /// </summary>
        private const ulong PlayerXuid = 2533274790395904UL + 0x57505200UL;

        private long _userIdentity;
        private long _serviceClient;
        private long _xboxUser;
        private long _userStatus;
        private long _userProfile;
        private long _leaderboardService;

        /// <summary>
        /// Builds the Xbox classes, if the class name is one of them.
        /// </summary>
        /// <remarks>
        /// Returned as the activation factory, not as an instance. That is not how a real
        /// device works - the image would activate first - but this runtime's QueryInterface
        /// answers every IID with the same object, so the factory is what the image ends up
        /// calling the interface on. The slot dump confirms it: `SignInAsync` arrives on the
        /// object handed back by `GetActivationFactory`, not on anything it returned.
        /// </remarks>
        private long? CreateXboxClass(string className) => className switch
        {
            // Behaviourally confirmed as the interface itself: the object handed back here
            // gets SignInAsync (slot 6, one out-parameter) and get_ServiceClient (slot 11), and
            // what SignInAsync returns gets put_Completed and GetResults. A factory whose slot
            // 6 was ActivateInstance would have produced a different second call.
            "Microsoft.Xbox.XboxLIVEService" => XboxLiveService(),

            // These four are used as factories - the image constructs instances through them -
            // so slot 6 has to be the factory method, and the instance is what it returns.
            // Getting this wrong was the loading screen: IUserFactory::CreateUser was answered
            // by IUser::get_Identity, the image took the UserIdentity it got back for a User,
            // called GetAchievementsAsync(0, 100) on it - slot 11 of an eight-slot object -
            // and the out-parameter nobody wrote became a null task, whose continuation the
            // image reports as "Unable to connect to Xbox".
            "Microsoft.Xbox.Foundation.ServiceClient" => ServiceClientFactory(),
            "Microsoft.Xbox.Foundation.UserIdentity" => UserIdentityFactory(),
            "Microsoft.Xbox.User" => XboxUserFactory(),
            "Microsoft.Xbox.Leaderboards.LeaderboardService" => LeaderboardServiceFactory(),
            _ => null,
        };

        /// <summary>
        /// <c>IUserFactory::CreateUser(UInt64 xuid, String gamertag)</c>, or
        /// <c>IActivationFactory::ActivateInstance()</c> - both slot 6, told apart by the
        /// first argument: an out-parameter for ActivateInstance, and for CreateUser the
        /// padding register before an 8-byte-aligned UInt64 in r2:r3, with the string at
        /// [sp] and the out-parameter at [sp+4].
        /// </summary>
        private long XboxUserFactory() => CreateDiscoveryObject(
            "IUserFactory",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("CreateUser", () =>
                {
                    WriteOut(ArmEmulator.IsStackAddress(Arg(1)) ? 1 : 5, XboxUser());
                    Return(HResultOk);
                }),
            });

        /// <summary><c>IUserIdentityFactory::CreateUserIdentity(UInt64, String)</c> - same layout.</summary>
        private long UserIdentityFactory() => CreateDiscoveryObject(
            "IUserIdentityFactory",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("CreateUserIdentity", () =>
                {
                    WriteOut(ArmEmulator.IsStackAddress(Arg(1)) ? 1 : 5, UserIdentity());
                    Return(HResultOk);
                }),
            });

        /// <summary><c>IServiceClientFactory::CreateServiceClient(String)</c> - one argument, out at r2.</summary>
        private long ServiceClientFactory() => CreateDiscoveryObject(
            "IServiceClientFactory",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("CreateServiceClient", () =>
                {
                    WriteOut(ArmEmulator.IsStackAddress(Arg(1)) ? 1 : 2, ServiceClient());
                    Return(HResultOk);
                }),
            });

        /// <summary>
        /// <c>ActivateInstance()</c> or <c>ILeaderboardFactory::Create(UInt32 titleId)</c> -
        /// the class has both constructors, and the image used the parameterless one
        /// (slot 6 with an out-parameter in r1).
        /// </summary>
        private long LeaderboardServiceFactory() => CreateDiscoveryObject(
            "ILeaderboardServiceFactory",
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("ActivateInstance", () =>
                {
                    WriteOut(ArmEmulator.IsStackAddress(Arg(1)) ? 1 : 2, LeaderboardService());
                    Return(HResultOk);
                }),
            });

        /// <summary>
        /// <c>Microsoft.Xbox.IXboxLIVEService</c> - the entry point, and the one the loading
        /// screen is waiting on.
        /// </summary>
        private long XboxLiveService() => CreateDiscoveryObject(
            "IXboxLIVEService",
            slotCount: 16,
            known: new Dictionary<int, (string, Action)>
            {
                // IAsyncOperation<UserIdentity> SignInAsync()
                [InspectableSlots + 0] = ("SignInAsync", () =>
                    ReturnObject(AsyncOperation("SignInAsync", UserIdentity()))),

                // IAsyncAction SignOutAsync()
                [InspectableSlots + 1] = ("SignOutAsync", () => ReturnObject(AsyncOperation("SignOutAsync", 0))),

                // EventRegistrationToken add_SignedOut(EventHandler<SignedOutEventArgs>)
                //
                // Registered and never raised, which is correct: the player does not sign out
                // because there is nobody to sign out. Raising it on registration - which an
                // earlier guess here did - tells the game the player left, in the middle of
                // signing them in.
                [InspectableSlots + 2] = ("add_SignedOut", AcceptEventHandler),
                [InspectableSlots + 3] = ("remove_SignedOut", () => Return(HResultOk)),

                // UserIdentity get_SignedInUserIdentity()
                [InspectableSlots + 4] = ("get_SignedInUserIdentity", () => ReturnObject(UserIdentity())),

                // ServiceClient get_ServiceClient()
                [InspectableSlots + 5] = ("get_ServiceClient", () => ReturnObject(ServiceClient())),

                // void InvalidateCacheGroup(CacheGroup)
                [InspectableSlots + 6] = ("InvalidateCacheGroup", () => Return(HResultOk)),

                // IAsyncAction ShowGamesApplicationAsync(LaunchAction) - one argument, so the
                // out-parameter is r2 rather than r1.
                [InspectableSlots + 7] = ("ShowGamesApplicationAsync", () =>
                {
                    WriteOut(2, AsyncOperation("ShowGamesApplicationAsync", 0));
                    Return(HResultOk);
                }),
            });

        /// <summary><c>Microsoft.Xbox.Foundation.IServiceClient</c>.</summary>
        private long ServiceClient() => _serviceClient != 0 ? _serviceClient : _serviceClient =
            CreateDiscoveryObject(
                "IServiceClient",
                slotCount: 16,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("SignInAsync", () =>
                        ReturnObject(AsyncOperation("ServiceClient.SignInAsync", 0))),
                    [InspectableSlots + 1] = ("SignOutAsync", () =>
                        ReturnObject(AsyncOperation("ServiceClient.SignOutAsync", 0))),
                    [InspectableSlots + 2] = ("add_SignedOut", AcceptEventHandler),
                    [InspectableSlots + 3] = ("remove_SignedOut", () => Return(HResultOk)),
                    [InspectableSlots + 4] = ("get_SignedInUserIdentity", () => ReturnObject(UserIdentity())),

                    // String get_Token() - the service ticket. Non-empty, because an empty one
                    // is what a failed sign-in produces.
                    [InspectableSlots + 5] = ("get_Token", () => ReturnString("WPR-OFFLINE-TOKEN")),

                    // UInt32 get_TitleId()
                    [InspectableSlots + 6] = ("get_TitleId", () => ReturnUInt32(1)),
                });

        /// <summary><c>Microsoft.Xbox.Foundation.IUserIdentity</c> - two members.</summary>
        private long UserIdentity() => _userIdentity != 0 ? _userIdentity : _userIdentity =
            CreateDiscoveryObject(
                "IUserIdentity",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    // UInt64 get_Xuid() - sixty-four bits through the out-parameter, which is
                    // the whole reason a placeholder object pointer was wrong here.
                    [InspectableSlots + 0] = ("get_Xuid", () =>
                    {
                        if (ArmEmulator.IsStackAddress(Arg(1)) || Arg(1) != 0)
                        {
                            _emulator.WriteUInt64(Arg(1), PlayerXuid);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 1] = ("get_Gamertag", () => ReturnString(PlayerGamertag)),
                });

        /// <summary><c>Microsoft.Xbox.IUser</c>.</summary>
        private long XboxUser() => _xboxUser != 0 ? _xboxUser : _xboxUser = CreateDiscoveryObject(
            "IUser",
            slotCount: 24,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Identity", () => ReturnObject(UserIdentity())),
                [InspectableSlots + 1] = ("get_Status", () => ReturnObject(UserStatus())),
                [InspectableSlots + 2] = ("GetProfileAsync", () =>
                    ReturnObject(AsyncOperation("GetProfileAsync", UserProfile()))),

                // SetProfileAsync(UserProfile) - one argument in, so the out is r2.
                [InspectableSlots + 3] = ("SetProfileAsync", () =>
                {
                    WriteOut(2, AsyncOperation("SetProfileAsync", UserProfile()));
                    Return(HResultOk);
                }),

                [InspectableSlots + 4] = ("GetFriendsAsync", () =>
                    ReturnObject(AsyncOperation("GetFriendsAsync", Collection("UserCollection")))),

                // GetAchievementsAsync(UInt32, UInt32, Boolean, AchievementCollection) - four
                // arguments, so the out-parameter is the fifth and lands on the stack.
                [InspectableSlots + 5] = ("GetAchievementsAsync", () =>
                {
                    WriteOut(5, AsyncOperation("GetAchievementsAsync", Collection("AchievementCollection")));
                    Return(HResultOk);
                }),

                // UnlockAchievementAsync(UInt32)
                [InspectableSlots + 6] = ("UnlockAchievementAsync", () =>
                {
                    WriteOut(2, AsyncOperation("UnlockAchievementAsync", 0));
                    Return(HResultOk);
                }),

                [InspectableSlots + 7] = ("GetAvatarManifestAsync", () =>
                    ReturnObject(AsyncOperation("GetAvatarManifestAsync", 0))),
            });

        /// <summary><c>Microsoft.Xbox.IUserStatus</c> - presence, not sign-in state.</summary>
        private long UserStatus() => _userStatus != 0 ? _userStatus : _userStatus =
            CreateDiscoveryObject(
                "IUserStatus",
                slotCount: 12,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_IsOnline", Boolean(false)),
                    [InspectableSlots + 2] = ("get_TitleId", () => ReturnUInt32(1)),
                    [InspectableSlots + 3] = ("get_RichPresence", () => ReturnString(string.Empty)),
                    [InspectableSlots + 5] = ("get_DeviceType", () => ReturnString("WindowsPhone")),
                });

        /// <summary><c>Microsoft.Xbox.IUserProfile</c> - the gamer card.</summary>
        private long UserProfile() => _userProfile != 0 ? _userProfile : _userProfile =
            CreateDiscoveryObject(
                "IUserProfile",
                slotCount: 24,
                known: new Dictionary<int, (string, Action)>
                {
                    [InspectableSlots + 0] = ("get_AvatarImageUrl", () => ReturnString(string.Empty)),
                    [InspectableSlots + 1] = ("get_Bio", () => ReturnString(string.Empty)),
                    [InspectableSlots + 2] = ("put_Bio", () => Return(HResultOk)),
                    [InspectableSlots + 3] = ("get_GamerPictureUrl", () => ReturnString(string.Empty)),
                    [InspectableSlots + 4] = ("get_Gamerscore", () => ReturnUInt32(0)),
                    [InspectableSlots + 5] = ("get_Gamertag", () => ReturnString(PlayerGamertag)),
                    [InspectableSlots + 6] = ("get_HasAvatar", Boolean(false)),
                    [InspectableSlots + 7] = ("get_MembershipLevel", () => ReturnUInt32(0)),
                    [InspectableSlots + 8] = ("get_Location", () => ReturnString(string.Empty)),
                    [InspectableSlots + 10] = ("get_Motto", () => ReturnString(string.Empty)),
                    [InspectableSlots + 12] = ("get_Name", () => ReturnString(PlayerGamertag)),
                });

        /// <summary><c>Microsoft.Xbox.Leaderboards.ILeaderboardService</c>.</summary>
        private long LeaderboardService() => _leaderboardService != 0 ? _leaderboardService : _leaderboardService = CreateDiscoveryObject(
            "ILeaderboardService",
            slotCount: 12,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("GetLeaderboardsAsync", () =>
                    ReturnObject(AsyncOperation(
                        "GetLeaderboardsAsync", Collection("LeaderboardMetadataCollection")))),

                // GetLeaderboardAsync(UInt32, UInt32, UInt32, Boolean, IReadOnlyList<UInt32>,
                // Leaderboard) - six arguments, out-parameter seventh.
                [InspectableSlots + 1] = ("GetLeaderboardAsync", () =>
                {
                    WriteOut(7, AsyncOperation("GetLeaderboardAsync", Collection("Leaderboard")));
                    Return(HResultOk);
                }),

                // GetSystemLeaderboardAsync(UInt32, UInt32, String, Leaderboard) - four in.
                [InspectableSlots + 2] = ("GetSystemLeaderboardAsync", () =>
                {
                    WriteOut(5, AsyncOperation("GetSystemLeaderboardAsync", Collection("Leaderboard")));
                    Return(HResultOk);
                }),

                // PostResultAsync(UInt32, LeaderboardAggregation, Int64). The Int64 is why this
                // one is not simply "three arguments then the out": AAPCS aligns a 64-bit
                // argument to an even register pair, so it takes r2 and r3 only if r2 is even -
                // it is (this=r0, id=r1, aggregation=r2, so the Int64 skips r3 and goes on the
                // stack). The out-parameter follows it there.
                [InspectableSlots + 3] = ("PostResultAsync", () =>
                {
                    WriteOut(6, AsyncOperation("PostResultAsync", 0));
                    Return(HResultOk);
                }),

                // PostResultsAsync(UInt32, IReadOnlyList<LeaderboardAttribute>)
                [InspectableSlots + 4] = ("PostResultsAsync", () =>
                {
                    WriteOut(3, AsyncOperation("PostResultsAsync", 0));
                    Return(HResultOk);
                }),
            });

        /// <summary>
        /// An <c>IAsyncOperation&lt;T&gt;</c> that has already finished, carrying
        /// <paramref name="result"/>.
        /// </summary>
        /// <remarks>
        /// Members are <c>put_Completed</c>, <c>get_Completed</c>, <c>GetResults</c>. Nothing
        /// here is ever actually pending, so completing the instant a handler is attached is
        /// not a shortcut - it is accurate.
        /// <para>
        /// <c>IAsyncAction</c> has the same three members with a <c>GetResults</c> that returns
        /// nothing, so a zero result covers both.
        /// </para>
        /// </remarks>
        private long AsyncOperation(string name, long result)
        {
            long[] self = new long[1];
            long[] asyncInfo = [AsyncInfo(name)];

            long operation = CreateDiscoveryObject(
                $"IAsyncOperation<{name}>",
                slotCount: 8,
                known: new Dictionary<int, (string, Action)>
                {
                    // IAsyncOperation and IAsyncInfo are different interfaces on the same
                    // object, and their members collide: get_Status is IAsyncInfo member 1,
                    // which is get_Completed on IAsyncOperation. Answering every IID with the
                    // same object therefore tells a caller that asks for IAsyncInfo that the
                    // operation's status is whatever get_Completed returned - zero, which is
                    // AsyncStatus::Started. It waits for ever on an operation that finished
                    // before it asked.
                    [0] = ("QueryInterface", () =>
                    {
                        long answer = IsAsyncInfo(Arg(1)) ? asyncInfo[0] : self[0];
                        if (Arg(2) != 0)
                        {
                            _emulator.WriteUInt32(Arg(2), (uint)answer);
                        }

                        Return(HResultOk);
                    }),
                    [InspectableSlots + 0] = ("put_Completed", () => CompleteAsyncAction(self[0], Arg(1))),
                    [InspectableSlots + 1] = ("get_Completed", () => ReturnObject(0)),
                    [InspectableSlots + 2] = ("GetResults", () =>
                    {
                        if (result != 0)
                        {
                            ReturnObject(result);
                            return;
                        }

                        // IAsyncAction::GetResults() has no out-parameter at all. Writing one
                        // would scribble on whatever the caller had in r1.
                        Return(HResultOk);
                    }),
                });

            self[0] = operation;
            return operation;
        }

        /// <summary>
        /// <c>IAsyncInfo</c> for an operation that has already finished successfully.
        /// </summary>
        /// <remarks>
        /// Members are Id, Status, ErrorCode, Cancel, Close. This is what ppltasks reads when
        /// an image wraps an async operation in a task rather than attaching a handler to it,
        /// which is exactly what this title does with `GetLeaderboardsAsync` - it takes the
        /// operation, references it four times, and never calls put_Completed at all.
        /// </remarks>
        private long AsyncInfo(string name) => CreateDiscoveryObject(
            $"IAsyncInfo<{name}>",
            slotCount: 12,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Id", () => ReturnUInt32(1)),

                // AsyncStatus::Completed. Zero here is Started, and Started never ends.
                [InspectableSlots + 1] = ("get_Status", () => ReturnUInt32(AsyncStatusCompleted)),
                [InspectableSlots + 2] = ("get_ErrorCode", () => ReturnUInt32(0)),
                [InspectableSlots + 3] = ("Cancel", () => Return(HResultOk)),
                [InspectableSlots + 4] = ("Close", () => Return(HResultOk)),
            });

        /// <summary>The IID of <c>IAsyncInfo</c>, {00000036-0000-0000-C000-000000000046}.</summary>
        private static readonly Guid AsyncInfoIid = new("00000036-0000-0000-C000-000000000046");

        private bool IsAsyncInfo(long iid)
        {
            if (iid == 0)
            {
                return false;
            }

            try
            {
                return new Guid(_emulator.ReadMemory(iid, 16)) == AsyncInfoIid;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>An empty collection: <c>get_Items</c> and <c>get_TotalRecords</c>.</summary>
        private long Collection(string name) => CreateDiscoveryObject(
            name,
            slotCount: 8,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Items", () => ReturnObject(EmptyVectorView())),
                [InspectableSlots + 1] = ("get_TotalRecords", () => ReturnUInt32(0)),
            });

        /// <summary>
        /// An empty collection that answers as <c>IVectorView&lt;T&gt;</c> and as
        /// <c>IIterable&lt;T&gt;</c>, because a C++/CX caller uses either.
        /// </summary>
        /// <remarks>
        /// The two interfaces collide at slot 6 - <c>GetAt(index, out)</c> on the view,
        /// <c>First(out)</c> on the iterable - and this runtime's QueryInterface hands back one
        /// object for both. They are told apart by the argument: First has its out-parameter
        /// in r1, GetAt has an index there and the out-parameter in r2. A <c>for each</c> over
        /// an empty collection goes through First and then asks the iterator whether it has a
        /// current item; failing First instead leaves the caller with an iterator pointer that
        /// was never written, and the next thing it does is jump through it.
        /// <para>
        /// The first version of this declared IndexOf and GetMany at slots 8 and 9 in a
        /// vtable eight slots long. A call to either read the next heap block as code.
        /// </para>
        /// </remarks>
        private long EmptyVectorView() => CreateDiscoveryObject(
            "IVectorView",
            slotCount: 12,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("GetAt|First", () =>
                {
                    if (ArmEmulator.IsStackAddress(Arg(1)))
                    {
                        // IIterable::First(IIterator** out)
                        WriteOut(1, EmptyIterator());
                        Return(HResultOk);
                        return;
                    }

                    // IVectorView::GetAt(index, T* out) on an empty view: E_BOUNDS, and nothing
                    // in the out-parameter, because there is nothing.
                    WriteOut(2, 0);
                    Return(HResultBounds);
                }),
                [InspectableSlots + 1] = ("get_Size", () => ReturnUInt32(0)),
                [InspectableSlots + 2] = ("IndexOf", () =>
                {
                    // (value, UINT32* index, boolean* found) - say not found, twice.
                    WriteOut(2, 0);
                    if (Arg(3) != 0)
                    {
                        _emulator.WriteBoolean(Arg(3), false);
                    }

                    Return(HResultOk);
                }),
                [InspectableSlots + 3] = ("GetMany", () =>
                {
                    WriteOut(3, 0);
                    Return(HResultOk);
                }),
            });

        /// <summary>
        /// <c>IIterator&lt;T&gt;</c> over nothing: get_Current, get_HasCurrent, MoveNext, GetMany.
        /// </summary>
        private long EmptyIterator() => CreateDiscoveryObject(
            "IIterator",
            slotCount: 12,
            known: new Dictionary<int, (string, Action)>
            {
                [InspectableSlots + 0] = ("get_Current", () =>
                {
                    WriteOut(1, 0);
                    Return(HResultBounds);
                }),
                [InspectableSlots + 1] = ("get_HasCurrent", Boolean(false)),
                [InspectableSlots + 2] = ("MoveNext", Boolean(false)),
                [InspectableSlots + 3] = ("GetMany", () =>
                {
                    WriteOut(3, 0);
                    Return(HResultOk);
                }),
            });

        /// <summary>E_BOUNDS, what a WinRT collection answers for an index it does not have.</summary>
        private const int HResultBounds = unchecked((int)0x8000000B);

        private long _platformExceptionVtable;

        /// <summary>
        /// A vtable for a <c>Platform::Exception</c> the image has just constructed.
        /// </summary>
        /// <remarks>
        /// vccorlib's exception constructors are imports, and an import nobody implements
        /// returns zero and writes nothing - so the object the image allocated for the
        /// exception keeps whatever its memory held before. The image then throws it, and the
        /// first thing the throw does is read the vtable. One recycled block began with its own
        /// address, and the run ended with the CPU executing a list sentinel. Every slot here is
        /// a trap that answers S_OK, so a virtual call on the exception is logged and survived.
        /// </remarks>
        public long PlatformExceptionVtable()
        {
            if (_platformExceptionVtable == 0)
            {
                long prototype = CreateDiscoveryObject("Platform::Exception", slotCount: 24);
                _platformExceptionVtable = _emulator.ReadUInt32(prototype, 0);
            }

            return _platformExceptionVtable;
        }

        /// <summary>
        /// Accepts an event handler and hands back a zero registration token.
        /// </summary>
        private void AcceptEventHandler()
        {
            if (Arg(2) != 0)
            {
                _emulator.WriteUInt64(Arg(2), 0);
            }

            Return(HResultOk);
        }

        /// <summary>Writes a pointer through the out-parameter at a given argument index.</summary>
        private void WriteOut(int index, long value)
        {
            if (Arg(index) != 0)
            {
                _emulator.WriteUInt32(Arg(index), (uint)value);
            }
        }
    }
}
