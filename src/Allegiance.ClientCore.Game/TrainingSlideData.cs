namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P785 — the native slideshow slides, extracted verbatim from the artwork
/// corpus's training MDLs (the commit pinned in packaging/sources.json): the
/// <c>slides</c> list of each tm_*.mdl — native's (image, SoundID) pairs shown by
/// WinTrek/Slideshow.cpp (namespaces chosen at WinTrek.cpp:2473-2510). The port
/// draws each slide's native narration/on-screen text (a visual stand-in for the
/// full-screen slide image) and plays the slide's tm_slide_* voice clip from the
/// artwork install. An empty caption is a native image-only slide (the post-mission
/// slides), drawn with the slideshow title only.
/// </summary>
public static class TrainingSlideData
{
    /// <summary>(caption, sound stem) per slide, in native order.</summary>
    public static (string Caption, string SoundStem)[] Slides(int missionId, bool post) =>
        (missionId, post) switch
        {
            (1, true) => new (string, string)[] {
                ("Welcome, Recruits, to the Iron Coalition's Elgin Training Center, in the outer Neptune sector.", "tm_slide_1_01"),
                ("As you are aware, almost 10 years of strife have passed since the cataclysm that destroyed Earth.\n\nIt is a desperate time for us all: GigaCorp forces are exploiting their seemingly endless assets to dominate surviving humanity. The genetically mutated Bios have emerged to forcibly drag the human race into an evolutionary watershed. The cutthroat Belters are uniting to resist the order the Iron Coalition strives to bring.\n\nIt is humanity's darkest hour. With your help and that of your teammates, we can restore order and discipline.", "tm_slide_1_02"),
                ("During your stay at the training center, you will be given several opportunities to master different aspects of space flight, combat, and command operations. We will begin with a basic tour of this space station.", "tm_slide_1_03"),
                ("This is the hangar.", "tm_slide_1_04a"),
                ("Clicking here, or pressing the F1 key, brings up the Help display system.", "tm_slide_1_04b"),
                ("Use this system?it has excellent detailed information not available anywhere else.", "tm_slide_1_05"),
                ("When operating as a combat pilot, you will primarily be concerned with these two areas: launch and loadout.", "tm_slide_1_06"),
                ("Clicking here takes you directly to the hangar door for a rapid launch.", "tm_slide_1_07"),
                ("Clicking here, or pressing the F4 key, takes you to the Loadout screen.", "tm_slide_1_08"),
                ("The Loadout screen is where you choose your ship, its weaponry, and its cargo. We'll cover the use of the Loadout screen in training mission 3, Basic Weaponry.", "tm_slide_1_09"),
                ("These areas of the hangar are primarily for use by the team commander. If you are not in line for officer duty, don't worry about them. If you want to try your hand as an officer, these topics will be covered in training mission 5, Command View.", "tm_slide_1_10"),
                ("Clicking here, or pressing the F8 key, brings up the Teleport display.", "tm_slide_1_11"),
                ("You use the Teleport display whenever you want to jump to a different space station or to board another ship as a turret gunner.", "tm_slide_1_12"),
                ("To join another ship as a turret gunner, you select the ship you want to board and then click the Transport button. The numbers on the right side of the display show you how many turret slots are manned by players and how many total turrets the ship has.", "tm_slide_1_13"),
                ("Got all that? ? Remember, you can bring up the Help display at any time. Right now, they are waiting for you on the hangar deck. Get your gear and proceed to training mission 2, Basic Flight. Good luck.", "tm_slide_1_14"),
            },
            (2, true) => new (string, string)[] {
                ("", "tm_slide_2_post_01"),
            },
            (2, false) => new (string, string)[] {
                ("In this mission, you will receive a basic introduction to your controls and heads-up display and do a little bit of flying.", "tm_slide_2_01"),
            },
            (3, true) => new (string, string)[] {
                ("", "tm_slide_3_post_01"),
            },
            (3, false) => new (string, string)[] {
                ("Before launching into a combat scenario, you should select the ship you wish to fly and outfit it in the way best suited to the operation you are planning.", "tm_slide_3_01"),
                ("Clicking here, or pressing the F4 key, takes you to the Loadout screen to do just that.", "tm_slide_3_02"),
                ("Here, you can select from the ships currently available to your team.", "tm_slide_3_03"),
                ("Clicking here cycles through the ships that are available to you at this station.", "tm_slide_3_04"),
                ("This is where you select which weapons you wish to use.", "tm_slide_3_05"),
                ("This is where you select a shield.", "tm_slide_3_06"),
                ("Clicking here allows you to select what type of expendables this craft will be using, such as mines and probes and so on.", "tm_slide_3_07"),
                ("This is where you select the type of missiles this ship will use.", "tm_slide_3_08"),
                ("All ship types vary in the types of weapons, equipment, expendables, and missiles they can use. For example, the scout cannot carry a tactical nuclear missile, so one won?t be available to your scout in the Loadout screen. You would need to fly a different type of ship, such as an Missile Frigate to be able to use a nuclear missile.", "tm_slide_3_09"),
                ("You can put anything in your cargo, though. Be careful putting things in cargo because their mass affects your agility in space. If you load up expendables, like ammo, fuel, missiles, and probes, in cargo, then the ship will automatically use them when its current magazine runs out.", "tm_slide_3_10"),
                ("All of the items you select in loadout are shown in the Inventory display while in flight.", "tm_slide_3_11"),
                ("You should pay attention to the changing characteristics of the craft as it is outfitted?different configs can have a significant effect on your ship?s performance.", "tm_slide_3_12"),
                ("Click the Launch button when you are ready.", "tm_slide_3_13"),
            },
            (4, true) => new (string, string)[] {
                ("", "tm_slide_4_post_01"),
            },
            (4, false) => new (string, string)[] {
                ("The most important aspect of all our operations is the collection of the resource Helium3. Drone miners tend to the Helium3 asteroids located in deep space. When the miners return safely, we can expand our operations, by purchasing new equipment, ships, and stations.", "tm_slide_4_01"),
                ("The protection of friendly mining units is imperative to your survival in remote sectors of space. The elimination of enemy miners is of obvious tactical benefit.", "tm_slide_4_02"),
            },
            (5, true) => new (string, string)[] {
                ("? or you can click here.", "tm_slide_5_post_01"),
                ("The last thing I?ll touch on this time around is the Game State display. Pressing G brings this up. This will tell you if you are winning or losing. It looks like we?re winning big, so let?s call it a day.", "tm_slide_5_post_02"),
                ("The Help display has more detailed coverage of this material. You can access it by pressing the F1 key while in a mission.", "tm_slide_5_post_03"),
                ("Now, I think it?s time you had some tense combat experience. Proceed to mission 6, practice arena.", "tm_slide_5_post_04"),
            },
            (5, false) => new (string, string)[] {
                ("Clicking here, or pressing the F3 key, in the hangar takes you to Command view.", "tm_slide_5_01"),
                ("This is the sector overview. From here, you can see everything that?s happening in this sector. If you are piloting a ship, your view is limited to the sector that your ship is in. In Command view, you can view all sectors.", "tm_slide_5_02"),
                ("Use the mini Map display to choose which sector you want to look at-- click the sector you want to view.", "tm_slide_5_03"),
                ("Notice that there?s Helium3 in this sector. You?ll want to mine that ? but first you?ll have to buy a miner.", "tm_slide_5_04"),
                ("Click here, or press the F5 key, to bring up the Investments display.", "tm_slide_5_05"),
                ("This is the Investments display. This is where you buy new technology, new ships, and constructors for expanding your dominance. Consult the Help display or the Survival Card for an overview of the technology tree.", "tm_slide_5_06"),
                ("Here we?ve scrolled the list of items you can buy so that the miner is visible. In this scenario, miners cost 4000 credits. We have enough, so we?ll go for it.", "tm_slide_5_07"),
                ("Click the Buy button to buy the miner ?", "tm_slide_5_08"),
                ("? and wait until it?s created.", "tm_slide_5_09"),
                ("Miner reporting for duty.", "droneReport4Duty"),
                ("The miner will automatically go mine Helium3 if there are Helium3 asteroids in secure sectors, but it may not pick the best place to go. We?ll talk about how you can help the miner perform better in a little bit. For now, let?s go over buildings.", "tm_slide_5_11"),
                ("After you mine for a while, you?ll accumulate some cash. Building stations can also make cash available, but it?s much faster to mine helium.", "tm_slide_5_12"),
                ("We?ll use that money to buy a new station that we can get more technology from. Choose the station you would like to construct and click the Invest button.", "tm_slide_5_13"),
                ("After a few moments of preparation, the constructor will launch from your main starbase.", "tm_slide_5_14"),
                ("Now you have to tell the constructor where to build the new station. Some stations require special asteroids to provide the raw materials they need. Others can build on generic asteroids. This Expansion Complex constructor requires a uranium rock, a ?red rock? to build on. Refer to your Survival Card for the special rocks needed by other constructors.", "tm_slide_5_15"),
                ("You click the constructor to highlight it. Notice that some of the sectors in the Map display have a red circle around them. These are sectors that have the special rock you need.", "tm_slide_5_16"),
                ("With the constructor highlighted, you can right-click one of the sectors with the mouse to order the constructor to go there.", "tm_slide_5_17"),
                ("Wait while the constructor navigates the fastest route. Remember this is valuable and fragile cargo, so don?t send it undefended.", "tm_slide_5_18"),
                ("Now you?ll want to decide exactly where to build the Expansion Complex.", "tm_slide_5_19"),
                ("This is the Command bar. Ordering craft around is accomplished by selecting them by left-clicking them, left-clicking the command you want to give them, and then right-clicking in space where you want them to go.", "tm_slide_5_20"),
                ("Here we want the constructor to build on the uranium rock, so we left-click the constructor.", "tm_slide_5_21"),
                ("We left-click on the Build command.", "tm_slide_5_22"),
                ("Then we right-click the uranium asteroid. The constructor proceeds to the rock and commences building.", "tm_slide_5_23"),
                ("In many cases, ships will have default orders for different kinds of targets. You can issue the smart order by left-clicking the ship and then right-clicking where you want it to go. This is faster than using the command bar, but it may not always do exactly what you want.", "tm_slide_5_24"),
                ("Notice that the Expansion Complex button in the Investments display is now available, allowing you to buy the technology there.", "tm_slide_5_25"),
                ("Okay, I know that was a lot, and we still have more to cover, but we?ll do the rest of this in an interactive simulation.", "tm_slide_5_26"),
            },
            (6, true) => new (string, string)[] {
                ("", "tm_slide_6_post_01"),
            },
            (6, false) => new (string, string)[] {
                ("Combat Practice Simulation.\n\nTry to kill at least one wave of enemies to consider yourself ready for real combat", "tm_slide_6_01"),
            },
            (8, true) => new (string, string)[] {
                ("If you managed to protect the bomber long enough to destroy the base, well done.\n\nIf not, try again, that is the point of training.", "tm_slide_8_post_01"),
                ("If you had trouble, make sure you stay close to the bomber.  Using your sidethrusters may help you.  Also cross-nanning, nanning other nans to keep them alive often makes things easier.", "tm_slide_8_post_02"),
                ("Smart defenders generally kill the nans before attacking the bomber. One nan is usually not enough for a successful bomb run.", "tm_slide_8_post_03"),
                ("The Nanite Repair System can be used to repair most ships and stations...including the enemys' so be careful.", "tm_slide_8_post_04"),
            },
            (8, false) => new (string, string)[] {
                ("The term 'nanning' refers to the process of repairing a ship or station using the Nanite Repair System, which is typically mounted on a scout just as a gattling gun would be.\n\n(Tip: You can skip these slides with your movement keys.)", "tm_slide_8_01"),
                ("You will pilot a Rixian Unity scout in this simulation and be asked to nan in many of the same situations you will encounter in real missions.\n\nOne unique property of the Rixian Unity faction is their ships turn quickly on the horizontal x axis (yaw), but slowly on the vertical y axis (pitch).  Rolling your ship so that you make turns along the horizontal axis will give you an advantage.", "tm_slide_8_02"),
                ("Nans fill many important roles.\n\nThey help keep miners and constructors alive when they come under attack. They are able to bring a station's hull back to full strength after a failed attack by your enemy.  Finally, nans are a key support component during bomb runs. Without support, a bomber is an easy target for defenders.", "tm_slide_8_03"),
                ("'Nanning' is effective because while a ship is being repaired it is difficult for defenders to damage its hull.\n\nThis forces defenders to attack the nans first, extending the life of their target significantly.", "tm_slide_8_04"),
                ("The Nanite Repair System repairs a ship's hull, but not shield.  Firing it consumes energy, so only use it when your target's hull is damaged to avoid wasting energy.\n\nIn this mission, you will learn the basics of nanning and put your new skill to use in realistic situations.  Be aware that unlike other training missions, your custom controls will automatically be loaded if you have set them.\n\nGood luck!", "tm_slide_8_05"),
            },
            _ => System.Array.Empty<(string, string)>(),
        };
}
