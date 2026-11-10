using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HideoutInteriors
{
    /// <summary>
    /// What the hideout's things are called and which part of the decor panel they go in. The scene's own names are the
    /// artists' (bed_metal_rezerv, scontainer_gunsafe_tall, Helloween_Bat_03): a table turns the common ones into plain
    /// names and sorts them into categories, and anything it doesn't know is tidied up by rule (prefixes and LOD or
    /// version tags off, a few Russian words translated) and sorted by the words in it.
    /// </summary>
    internal static partial class Interiors
    {
        // The panel's categories, in the order they're listed. "All" first, then mods' own, then Hidden.
        private static readonly string[] Kinds =
        {
            "All", "Furniture", "Storage", "Lighting", "Electronics", "Kitchen", "Medical", "Weapons & gear", "Gym & range",
            "Tools", "Clothing", "Books & paper", "Decoration", "Seasonal", "Pets", "Bathroom", "Pipes & cables", "Building",
            "Junk", "Other", "Hidden",
        };

        // Not things to put in: the scene's colliders, markers, room shells, decals and stand-ins.
        private static readonly Regex NotForPanel = new Regex(
            @"collider|colider|coliider|raycast|_trigger|^cube$|^quad$|^sphere$|^green sphere$|^gameobject|^model(_lod)?$|^\dmodel|^shadow|^level\d|^\dlevel|^fbx$|point light|spot light|^light$|^!light|^!multiflare|culling|projectile|projection|decal|graffiti|^dust|stucco|crack|^basement_(room|paint)|^bunker_(vp|ceiling|floor)|new_bunker_wall|test_bunker|stub_basement|mainareanew|^root\d|^spawnpoint|^dummylight|ballistic|^circle|^wind|^water_part|target_control|control_interface|^advlight|light_in_export|concreate_tex|_noise|^weaponstest|^weapon_stand_lvl3_emissive",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Base name (lower case, no " (n)") -> name and category. A pattern ending in * matches anything that starts so.
        private static readonly (string pattern, string name, string kind)[] NameTable =
        {
            // ---- furniture
            ("armchair_old*", "Old armchair", "Furniture"), ("armchair*", "Armchair", "Furniture"), ("bag_chair*", "Bean bag", "Furniture"),
            ("bed_metal_bunk*", "Metal bunk bed", "Furniture"), ("bed_metal*", "Metal bed", "Furniture"), ("bed", "Bed", "Furniture"),
            ("bedsheet*", "Bed sheet", "Furniture"), ("matress*", "Mattress", "Furniture"), ("sofa_modul*", "Sofa section", "Furniture"),
            ("sofa*", "Sofa", "Furniture"), ("school_bench*", "School desk", "Furniture"), ("dressing_room_bench*", "Changing room bench", "Furniture"),
            ("bench", "Bench", "Furniture"), ("night_stand*", "Nightstand", "Furniture"), ("nightstand*", "Nightstand", "Furniture"),
            ("coffee_table*", "Coffee table", "Furniture"), ("old_table*plane", "Old table top", "Furniture"), ("old_table_side", "Old side table", "Furniture"),
            ("old_table*", "Old table", "Furniture"), ("table_bank*", "Office desk", "Furniture"), ("table_planks", "Plank table", "Furniture"),
            ("metal_table*", "Metal table", "Furniture"), ("folding_table*", "Folding table", "Furniture"), ("table_lamp*", "Desk lamp", "Lighting"),
            ("table*", "Table", "Furniture"),
            ("folding_chair*", "Folding chair", "Furniture"), ("office_chair*", "Office chair", "Furniture"), ("chair_crush*", "Broken chair", "Furniture"),
            ("chair*", "Chair", "Furniture"), ("stool*", "Stool", "Furniture"), ("cupboard_02*", "Wardrobe", "Furniture"),
            ("cupboard_metal*", "Metal cupboard", "Furniture"), ("cupboard*", "Cupboard", "Furniture"), ("wardrobe_shelf*", "Wardrobe shelf", "Furniture"),
            ("persian_carpet*", "Persian rug", "Furniture"), ("carpet_hideout*", "Hideout rug", "Furniture"), ("carpet_indoor*", "Rug", "Furniture"),
            ("carpets", "Rolled-up rugs", "Furniture"), ("carpet*", "Rug", "Furniture"), ("karpet*", "Rug", "Furniture"),
            ("curtains*", "Curtains", "Furniture"), ("pillows*", "Pillows", "Furniture"), ("cushion*", "Cushion", "Furniture"),
            ("blanket*", "Blanket", "Furniture"), ("adik_fitting_tablebox*", "Shop counter", "Furniture"), ("babycarriage", "Pram", "Furniture"),

            // ---- storage
            ("wooden_shelf*", "Wooden shelf", "Storage"), ("shelves_store*", "Shop shelving", "Storage"), ("shelf*", "Shelf", "Storage"), ("shelves*", "Shelves", "Storage"),
            ("rack_shelf*", "Shelving rack", "Storage"), ("rack_corner*", "Corner rack", "Storage"),
            ("box_closed*", "Closed box", "Storage"), ("boxe3_closed*", "Closed box", "Storage"), ("box_plastic*", "Plastic crate", "Storage"),
            ("boot_box*", "Shoe box", "Storage"), ("cardboard_box*_broken", "Crushed cardboard box", "Storage"), ("cardboard_box*_open", "Open cardboard box", "Storage"),
            ("cardboard_set*", "Cardboard boxes", "Storage"), ("cardboard_box*", "Cardboard box", "Storage"), ("carton_*", "Cardboard box", "Storage"),
            ("warehouse_boxes*", "Stacked boxes", "Storage"), ("metallbox_small*", "Small metal box", "Storage"), ("metallbox*", "Metal box", "Storage"),
            ("wood_box_crash*", "Broken wooden crate", "Storage"), ("wood_box_closed*", "Wooden crate", "Storage"), ("wood_box*", "Wooden crate", "Storage"),
            ("reserve_woodbox_small*", "Small wooden crate", "Storage"), ("weapon_box_single*", "Rifle crate", "Storage"), ("weapon_box*", "Weapon crate", "Storage"),
            ("military_ammobox122mm*", "122 mm shell crate", "Storage"), ("scontainer_ammo_grenade*", "Grenade box", "Storage"),
            ("scontainer_ammo_wood*", "Wooden ammo box", "Storage"), ("scontainer_gunrack*", "Gun rack", "Storage"),
            ("scontainer_gunsafe_big*", "Large gun safe", "Storage"), ("scontainer_gunsafe_pistol*", "Pistol safe", "Storage"),
            ("scontainer_gunsafe_tall*", "Tall gun safe", "Storage"), ("scontainer_gunsafe_valberg*", "Valberg safe", "Storage"),
            ("scontainer_medbag*", "Medbag SMU06", "Medical"), ("scontainer_medcase*", "Medcase", "Medical"),
            ("scontainer_toolbox*", "Toolbox", "Tools"), ("plastic_suitcase*", "Plastic case", "Storage"), ("suitcase*", "Suitcase", "Storage"),
            ("plastic_container*", "Plastic container", "Storage"), ("food_barrel*", "Food barrel", "Storage"),
            ("barrel_metal_cover*", "Lidded metal barrel", "Storage"), ("barrel_metal*", "Metal barrel", "Storage"), ("metal_barrel*", "Metal barrel", "Storage"),
            ("barrel_plastic*", "Plastic barrel", "Storage"), ("barrel_heap*", "Pile of barrels", "Storage"), ("pallet_barrel*", "Barrels on a pallet", "Storage"),
            ("pallete*", "Pallet", "Storage"), ("beer_barrel*", "Beer keg", "Kitchen"), ("kidsbag*", "Child's backpack", "Storage"),
            ("bag_sport*", "Sports bag", "Storage"), ("bagdead*", "Body bag", "Junk"), ("bag_garbage*", "Bin bag", "Junk"),
            ("item_container_secured_kappa*", "Secure container Kappa", "Storage"), ("item_container_items_thicc*", "THICC item case", "Storage"),
            ("item_container_weapon_thicc*", "THICC weapon case", "Storage"), ("item_container_weaponcase*", "Weapon case", "Storage"),
            ("item_container_pistol_case*", "Pistol case", "Storage"), ("item_container_magbox*", "Magazine case", "Storage"),
            ("item_container_ammo*", "Ammunition case", "Storage"), ("item_container_smallitems*", "Small S I C C case", "Storage"),
            ("item_container_trackcase*", "Hard case", "Storage"), ("item_double_gun_case*", "Double gun case", "Storage"),
            ("item_plano_pistol_case*", "Plano pistol case", "Storage"), ("keys_stend*", "Key board", "Storage"),

            // ---- weapons & gear
            ("weaponboard*", "Weapon board", "Weapons & gear"), ("showcaseweapon*", "Weapon showcase", "Weapons & gear"),
            ("weapon_stand*", "Weapon stand", "Weapons & gear"), ("weponstend*", "Weapon stand", "Weapons & gear"), ("wepon_stend*", "Weapon stand", "Weapons & gear"),
            ("stendmaniken*", "Mannequin stand", "Weapons & gear"), ("mannequin*", "Mannequin", "Weapons & gear"),
            ("item_ammo_box*", "Ammo box", "Weapons & gear"), ("patron_lamp*", "Cartridge lamp", "Lighting"), ("patron_vog25*", "VOG-25 grenade", "Weapons & gear"),
            ("patron_*", "Cartridge", "Weapons & gear"), ("rifle_shells*", "Spent rifle cases", "Weapons & gear"), ("shotshell*", "Spent shotgun shells", "Weapons & gear"),
            ("mag_ak74*", "AK-74 magazine", "Weapons & gear"), ("mag_aug*", "AUG magazine", "Weapons & gear"), ("mag_fal*", "FAL magazine", "Weapons & gear"),
            ("mag_mp5*", "MP5 magazine", "Weapons & gear"), ("handguard_ak*", "AK handguard", "Weapons & gear"),
            ("weapon_izhmash_aks74u*", "AKS-74U", "Weapons & gear"), ("item_equipment_armor_6b23*", "6B23 body armour", "Weapons & gear"),
            ("item_equipment_armor_6b2*", "6B2 body armour", "Weapons & gear"), ("item_equipment_armor_hexatac*", "Hexatac plate carrier", "Weapons & gear"),
            ("item_equipment_armor_otv*", "OTV body armour", "Weapons & gear"), ("item_equipment_backpack*", "Backpack", "Weapons & gear"),
            ("item_equipment_facecover_ballistic*", "Ballistic mask", "Weapons & gear"), ("item_equipment_facecover_gasmask*", "Gas mask", "Weapons & gear"),
            ("item_equipment_headphones*", "Headset", "Weapons & gear"), ("item_equipment_headset*", "ComTac II headset", "Weapons & gear"),
            ("item_equipment_helmet*", "Helmet", "Weapons & gear"), ("item_equipment_plate*destroyed", "Wrecked armour plate", "Weapons & gear"),
            ("item_equipment_plate*", "Armour plate", "Weapons & gear"), ("item_equipment_rig*", "Chest rig", "Weapons & gear"),
            ("velcro*", "Velcro patches", "Weapons & gear"), ("patches_group*", "Velcro patches", "Weapons & gear"),

            // ---- lighting
            ("bunker_lamp*", "Bunker lamp", "Lighting"), ("lum_lamp_broken*", "Broken strip light", "Lighting"), ("lum_lamp*", "Strip light", "Lighting"),
            ("lamp_ceiling*", "Ceiling lamp", "Lighting"), ("light_cellplafonds*", "Ceiling light", "Lighting"), ("light_trackspot*", "Track spotlight", "Lighting"),
            ("lab_track_light*", "Track light", "Lighting"), ("lab_lamp*", "Lab ceiling lamp", "Lighting"), ("shopping_mall_ceiling_light*", "Panel light", "Lighting"),
            ("lightstend*", "Light stand", "Lighting"), ("floor_lamp*", "Floor lamp", "Lighting"), ("table_lamp*", "Desk lamp", "Lighting"),
            ("emergency_exit_light*", "Exit sign", "Lighting"), ("candle*", "Candle", "Lighting"), ("glowstick*", "Glow stick", "Lighting"),
            ("flashlight_portable*", "Camping lamp", "Lighting"), ("light_blue", "Blue light", "Lighting"), ("light_green", "Green light", "Lighting"),
            ("light_red", "Red light", "Lighting"), ("item_electr_eslamp*", "Energy-saving bulb", "Lighting"), ("bunker_switch*", "Light switch", "Lighting"),
            ("switch1", "Light switch", "Lighting"),

            // ---- electronics
            ("pc_block*", "PC tower", "Electronics"), ("pc_monitor*", "Monitor", "Electronics"), ("pc_keyboard*", "Keyboard", "Electronics"),
            ("pc_computer_mouse*", "Computer mouse", "Electronics"), ("laptop_open*", "Open laptop", "Electronics"), ("laptop*", "Laptop", "Electronics"),
            ("tv_lcd*", "Flat-screen TV", "Electronics"), ("tv", "Old TV", "Electronics"), ("platstation4*", "PlayStation 4", "Electronics"),
            ("stereo_system*", "Stereo", "Electronics"), ("loudspeakers*", "Speakers", "Electronics"), ("m3rt_radiostation*", "Radio station", "Electronics"),
            ("m3tr_radiohandset*", "Radio handset", "Electronics"), ("item_spec_radioset*", "Radio set", "Electronics"), ("printer*", "Printer", "Electronics"),
            ("typewriter*", "Typewriter", "Electronics"), ("camera_surv*", "Security camera", "Electronics"), ("camerastreet*", "Street camera", "Electronics"),
            ("target_monitors*", "Range monitors", "Gym & range"), ("electronica*", "Circuit boards", "Electronics"), ("electronic_box*", "Electronics box", "Electronics"),
            ("bitcoin_farm*", "Mining rig", "Electronics"), ("headphone*", "Headphones", "Electronics"), ("cd", "CD", "Electronics"),
            ("disk_update*", "Hard disk", "Electronics"), ("solar_generator*", "Solar generator", "Electronics"), ("generator_exhaust*", "Generator exhaust", "Pipes & cables"),
            ("generator", "Generator", "Electronics"), ("factory_battery*", "Old battery", "Electronics"), ("item_car_battery*", "Car battery", "Electronics"),
            ("item_power_supply*", "Power supply", "Electronics"), ("item_barter_electr_hdd*", "Hard drive", "Electronics"), ("item_barter_electr_ssd*", "SSD", "Electronics"),
            ("item_barter_electr_modem*", "Modem", "Electronics"), ("item_barter_electr_sas*", "SAS drive", "Electronics"),
            ("item_barter_electr_usbadapter*", "USB adapter", "Electronics"), ("item_flash_card*", "Flash drive", "Electronics"),
            ("socket*", "Wall socket", "Electronics"), ("switchboard*", "Switchboard", "Electronics"), ("electric_switchbox*", "Switch box", "Electronics"),
            ("rubilnik*", "Knife switch", "Electronics"), ("fuse_shelfs*", "Fuse cabinet", "Electronics"), ("fuse*", "Fuse box", "Electronics"),
            ("old_electrical_cabinet*", "Electrical cabinet", "Electronics"), ("old_electrical_atlas*", "Electrical panel", "Electronics"),
            ("patch_panel*", "Patch panel", "Electronics"), ("alarm_panel*", "Alarm panel", "Electronics"), ("tube_doorphone*", "Door phone", "Electronics"),
            ("electric_heater*", "Electric heater", "Electronics"), ("heater*", "Heater", "Electronics"), ("burzhuyka*", "Wood stove", "Kitchen"),
            ("fan_*", "Fan", "Electronics"), ("electric_teapot*", "Electric kettle", "Kitchen"), ("electric_stove*", "Electric stove", "Kitchen"),
            ("microwave*", "Microwave", "Kitchen"), ("washer*", "Washing machine", "Bathroom"), ("clothes_dryer*", "Tumble dryer", "Bathroom"),
            ("extractor_hood*", "Cooker hood", "Kitchen"), ("refregirator_lab*", "Lab fridge", "Medical"), ("refrigerator*", "Fridge", "Kitchen"), ("fridge*", "Fridge", "Kitchen"),

            // ---- kitchen
            ("pot", "Pot", "Kitchen"), ("pan", "Frying pan", "Kitchen"), ("teapot*", "Teapot", "Kitchen"), ("dish_set*", "Stack of plates", "Kitchen"),
            ("dishes_broken*", "Broken plates", "Kitchen"), ("dish_*", "Plate", "Kitchen"), ("cup_*", "Mug", "Kitchen"), ("fork*", "Fork", "Kitchen"),
            ("spoon*", "Spoon", "Kitchen"), ("military_kitchenware*", "Mess tins", "Kitchen"), ("military_podnos*", "Tray of mess tins", "Kitchen"),
            ("gasstove*", "Camping stove", "Kitchen"), ("cooler_bottle*", "Water cooler", "Kitchen"), ("vegetable_basket*", "Vegetable basket", "Kitchen"),
            ("pizza*", "Pizza box", "Kitchen"), ("cannedempty*", "Empty can", "Kitchen"), ("cannedvegetables*", "Canned vegetables", "Kitchen"),
            ("oat_flakes*", "Oat flakes", "Kitchen"), ("juice_aple*", "Apple juice", "Kitchen"), ("item_food_canned_beef*", "Canned beef stew", "Kitchen"),
            ("item_food_juice*", "Army juice", "Kitchen"), ("item_food_mre*", "MRE", "Kitchen"), ("item_food_oatmeal*", "Oatmeal", "Kitchen"),
            ("item_food_ration*", "Iskra ration", "Kitchen"), ("item_food_squashspread*", "Squash spread", "Kitchen"), ("item_food_sugar*", "Sugar", "Kitchen"),
            ("bottle_broken*", "Broken bottle", "Kitchen"), ("bottle_baltika*", "Baltika beer", "Kitchen"), ("bottle_heineken*", "Beer bottle", "Kitchen"),
            ("bottle_kellers*", "Beer bottle", "Kitchen"), ("bottle", "Bottle", "Kitchen"), ("wine_bottle*", "Wine bottle", "Kitchen"),
            ("champagne_bottle*", "Broken champagne bottle", "Kitchen"), ("glass_crush*", "Broken glass", "Junk"), ("shards_glass*", "Broken glass", "Junk"),
            ("glass", "Glass", "Kitchen"), ("ashtray*", "Ashtray", "Kitchen"), ("boozegenerator*", "Moonshine still", "Kitchen"),

            // ---- medical
            ("medkit*", "First aid kit", "Medical"), ("item_meds_analgin*", "Analgin", "Medical"), ("item_meds_automedkit*", "Car first aid kit", "Medical"),
            ("item_meds_bandage*", "Bandage", "Medical"), ("item_meds_medkit_ifak*", "IFAK", "Medical"), ("item_meds_splint*", "Splint", "Medical"),
            ("augmentin*", "Augmentin", "Medical"), ("dropper*", "IV drip", "Medical"), ("vial_stand*", "Vial rack", "Medical"), ("vial*", "Vial", "Medical"),
            ("lab_microscope*", "Microscope", "Medical"), ("lablibra*", "Lab scales", "Medical"), ("lab_stuff*", "Lab equipment", "Medical"),
            ("item_barter_medical_pile*", "Medical supplies", "Medical"), ("cloth_bint*", "Bandage roll", "Medical"),

            // ---- gym & range
            ("barbell*", "Barbell", "Gym & range"), ("dumbbells*", "Dumbbells", "Gym & range"), ("power_trainer*", "Weight machine", "Gym & range"),
            ("punching_bag*", "Punching bag", "Gym & range"), ("sport_bench*", "Weight bench", "Gym & range"), ("training_bench*", "Training bench", "Gym & range"),
            ("gym_sportmat*", "Gym mat", "Gym & range"), ("rubber_floor*", "Rubber floor tile", "Gym & range"), ("rubber_tile_floor*", "Rubber floor tiles", "Gym & range"),
            ("basket_ball*", "Basketball", "Gym & range"), ("foot_ball*", "Football", "Gym & range"), ("metal_target*", "Metal target", "Gym & range"),
            ("paper_target*", "Paper target", "Gym & range"), ("target_paper*", "Paper target", "Gym & range"), ("target_stand_broken*", "Broken target stand", "Gym & range"),
            ("target_stand*", "Target stand", "Gym & range"), ("shooting_range_popper*", "Popper target", "Gym & range"), ("popper_targets*", "Popper targets", "Gym & range"),
            ("shooting_range_rails*", "Target rail", "Gym & range"), ("rail_targets*", "Rail targets", "Gym & range"),

            // ---- clothing
            ("cloth_jacket*", "Jacket", "Clothing"), ("jacket*", "Jacket", "Clothing"), ("cloth_pants*", "Trousers", "Clothing"), ("cloth_shirt*", "Shirt", "Clothing"),
            ("cloth_glove*", "Gloves", "Clothing"), ("cloth_thrown_heap*", "Pile of clothes", "Clothing"), ("clothes_stack*", "Folded clothes", "Clothing"),
            ("clothes_socks*", "Socks", "Clothing"), ("boot_*", "Boot", "Clothing"), ("shoe_hall_boots*", "Boots", "Clothing"), ("shoe_hall_newsport*", "Trainers", "Clothing"),
            ("shoes_hall_sport*", "Trainers", "Clothing"), ("hanger_clothes*", "Clothes on a hanger", "Clothing"), ("hanger*", "Coat hanger", "Clothing"),
            ("hooks_clothes*", "Coat hooks", "Clothing"), ("towel*", "Towel", "Bathroom"), ("hook_for_towels*", "Towel hook", "Bathroom"),

            // ---- books & paper
            ("book_open*", "Open book", "Books & paper"), ("books_longset*", "Row of books", "Books & paper"), ("book_smallset*", "Few books", "Books & paper"),
            ("booksfall*", "Fallen books", "Books & paper"), ("books*", "Books", "Books & paper"), ("book*", "Book", "Books & paper"),
            ("magazine*", "Magazine", "Books & paper"), ("newspaper_trash*", "Newspaper litter", "Books & paper"), ("newspaper*", "Newspaper", "Books & paper"),
            ("scatter_paper*", "Scattered papers", "Books & paper"), ("paper_wall*", "Paper on the wall", "Books & paper"), ("paper_table*", "Papers", "Books & paper"),
            ("paper_box_musor*", "Paper litter", "Junk"), ("paper_musor*", "Paper litter", "Junk"), ("paper*", "Papers", "Books & paper"),
            ("folder*", "Folder", "Books & paper"), ("drawing*", "Child's drawing", "Books & paper"), ("map_factory*", "Factory map", "Books & paper"),
            ("marker_board*", "Whiteboard", "Books & paper"), ("poster_fireext*", "Fire safety poster", "Books & paper"), ("reserve_sport_poster*", "Sports poster", "Books & paper"),
            ("poster*", "Poster", "Books & paper"), ("factory_signs*", "Factory sign", "Books & paper"), ("item_thin_diary*", "Diary", "Books & paper"),
            ("item_copy_paper*", "Copy paper", "Books & paper"), ("pen_*", "Pen", "Books & paper"), ("pencil_stand*", "Pencil cup", "Books & paper"),
            ("pencil*", "Pencil", "Books & paper"),

            // ---- decoration, seasonal, pets
            ("cactus*", "Cactus", "Decoration"), ("vase*", "Vase", "Decoration"), ("dry_bush*", "Dry bush", "Decoration"), ("vetky*", "Branches", "Decoration"),
            ("deer_horns*", "Deer antlers", "Decoration"), ("dinotoy*", "Toy dinosaur", "Decoration"), ("funko_cat*", "Cat figurine", "Decoration"),
            ("item_horse_figurine*", "Horse figurine", "Decoration"), ("piggy_bank*", "Piggy bank", "Decoration"), ("frame_*", "Picture frame", "Decoration"),
            ("mirror*", "Mirror", "Decoration"), ("wall_clock*", "Wall clock", "Decoration"), ("firefighter*", "Firefighter figure", "Decoration"),
            ("cat_house*", "Cat house", "Pets"), ("pet_ball*", "Pet ball", "Pets"), ("pet_bed*", "Pet bed", "Pets"), ("pet_bowl*", "Pet bowl", "Pets"),
            ("pet_carry*", "Pet carrier", "Pets"), ("pet_feed*", "Bag of pet food", "Pets"), ("pet_mouse*", "Toy mouse", "Pets"), ("pet_tray*", "Litter tray", "Pets"),
            ("pine_christmas*", "Christmas tree", "Seasonal"), ("hideout_garland*", "Garland", "Seasonal"), ("hideout_halloween_pumpkin*", "Pumpkin", "Seasonal"),
            ("helloween_bat*", "Bat", "Seasonal"), ("decor_candlestick*", "Candlestick", "Seasonal"), ("ballon*", "Balloon", "Seasonal"), ("broom*", "Broom", "Tools"),

            // ---- bathroom
            ("bathroom_sink*", "Sink", "Bathroom"), ("metal_sink*", "Metal sink", "Bathroom"), ("sink*", "Sink", "Bathroom"), ("case_shower*", "Shower cubicle", "Bathroom"),
            ("shower*", "Shower", "Bathroom"), ("wood_toilet*", "Wooden toilet", "Bathroom"), ("unitaz*", "Toilet", "Bathroom"), ("biotoilet*", "Portable toilet", "Bathroom"),
            ("toilet_paper_pack*", "Toilet roll pack", "Bathroom"), ("toilet_paper*", "Toilet roll", "Bathroom"), ("toilet_roll*", "Toilet roll", "Bathroom"),
            ("soap*", "Soap", "Bathroom"), ("sponge*", "Sponge", "Bathroom"), ("item_toothpaste*", "Toothpaste", "Bathroom"),

            // ---- pipes & cables
            ("sliv*", "Floor drain", "Pipes & cables"), ("big_water_filter*", "Big water filter", "Pipes & cables"), ("water_filter*", "Water filter", "Pipes & cables"),
            ("water_system*", "Water system", "Pipes & cables"), ("pumpsmall*", "Pump", "Pipes & cables"), ("small_pump*", "Pump", "Pipes & cables"),
            ("airfilter_system*", "Air filter unit", "Pipes & cables"), ("item_barter_other_airfilter*", "Air filter", "Pipes & cables"),
            ("box_vent*", "Vent box", "Pipes & cables"), ("vent_tube*", "Vent duct", "Pipes & cables"), ("ventilation_pipe*", "Vent pipe", "Pipes & cables"),
            ("pipe_broken*", "Broken pipe", "Pipes & cables"), ("pipe_fixed*", "Pipe", "Pipes & cables"), ("pipeline_fittings_small_connector*", "Pipe joint", "Pipes & cables"),
            ("pipeline_fittings_small_valve*", "Valve", "Pipes & cables"), ("fuel_tube*", "Fuel hose", "Pipes & cables"), ("*_tube", "Tube", "Pipes & cables"),
            ("cablemetal*", "Steel cable", "Pipes & cables"), ("cable*", "Cable", "Pipes & cables"), ("provod*", "Cable", "Pipes & cables"),
            ("!wire_floor*", "Cable on the floor", "Pipes & cables"), ("wire_fastenings*", "Cable clips", "Pipes & cables"), ("wire*", "Cables", "Pipes & cables"),
            ("basement_room_wire*", "Cables", "Pipes & cables"), ("diesel_spg*", "Diesel tank", "Pipes & cables"), ("bensinespg*", "Petrol tank", "Pipes & cables"),
            ("kanistra_metall*", "Metal jerrycan", "Tools"), ("kanistra_plastic*", "Plastic jerrycan", "Tools"),

            // ---- building
            ("opora*", "Support bracket", "Building"), ("railing*", "Railing", "Building"), ("grate_metal*", "Metal grate", "Building"), ("ladder*", "Ladder", "Building"),
            ("stremeanka*", "Stepladder", "Building"), ("outside_door*", "Metal door", "Building"), ("inside_door_metal*", "Grille door", "Building"),
            ("inside_door_wood*", "Wooden door", "Building"), ("bunker_door*", "Bunker door", "Building"), ("turnstile*", "Turnstile", "Building"),
            ("hideout_wood_plank*", "Wooden plank", "Building"), ("wood_board*", "Wooden board", "Building"), ("woodplanks*", "Wooden planks", "Building"),
            ("plywood*", "Plywood sheet", "Building"), ("hideout_black_wall*", "Black wall panel", "Building"), ("city_generic_tarp*", "Tarp", "Building"),
            ("trash_viniltents*", "Plastic sheeting", "Building"), ("metall_patch*", "Metal patch", "Building"), ("metal carcass*", "Metal frame", "Building"),
            ("_new_brick_wall*", "Brick wall", "Building"), ("bundle_grid*", "Bundle of mesh", "Building"), ("protven*", "Vent hood", "Pipes & cables"),

            // ---- tools
            ("item_tools_pliers*", "Pliers", "Tools"), ("item_tools_screwdriver*", "Screwdriver", "Tools"), ("item_tools_toolset*", "Toolset", "Tools"),
            ("item_tools_wrench*", "Wrench", "Tools"), ("item_ducttape*", "Duct tape", "Tools"), ("item_insulation_tape*", "Insulating tape", "Tools"),
            ("item_screw_bolts*", "Bolts", "Tools"), ("item_screw_nuts*", "Nuts", "Tools"), ("screw_bolts_nuts*", "Bolts and nuts", "Tools"),
            ("item_barter_building_nails*", "Nails", "Tools"), ("item_barter_building_pressuregauge*", "Pressure gauge", "Tools"),
            ("item_barter_building_thermometer*", "Thermometer", "Tools"), ("item_barter_household_wd40*", "WD-40", "Tools"), ("item_chain*", "Chain", "Tools"),
            ("item_matches*", "Matches", "Tools"), ("item_other_powder*", "Gunpowder", "Tools"), ("shovel*", "Shovel", "Tools"), ("mop*", "Mop", "Tools"),
            ("brush*", "Brush", "Tools"), ("plastic_bucket*", "Plastic bucket", "Tools"), ("bucket*", "Bucket", "Tools"), ("vise*", "Vice", "Tools"),
            ("garb_grindstone*", "Grindstone", "Tools"), ("floorcloth*", "Floor cloth", "Tools"),

            // ---- junk
            ("heap_carton*", "Pile of cardboard", "Junk"), ("heap_conc*", "Pile of concrete", "Junk"), ("heap_junk*", "Pile of junk", "Junk"),
            ("heap_metall*", "Pile of scrap metal", "Junk"), ("heap_paints*", "Pile of paint cans", "Junk"), ("heap_plit*", "Pile of broken tiles", "Junk"),
            ("heap_scrap*", "Pile of scrap", "Junk"), ("garb_bankpaint*", "Paint can", "Junk"), ("garb_aerosol*", "Spray can", "Junk"),
            ("garb_metal_pipe*", "Scrap pipe", "Junk"), ("garb_metal_rod*", "Metal rod", "Junk"), ("garb_metal_bar*", "Metal bar", "Junk"),
            ("garb_metal_circle*", "Metal disc", "Junk"), ("garb_*", "Scrap metal", "Junk"), ("bricks_rubble*", "Brick rubble", "Junk"),
            ("bricks*", "Loose bricks", "Junk"), ("plite_fragment*", "Broken tiles", "Junk"), ("flinders_wood*", "Wood splinters", "Junk"),
            ("firewood_pile*", "Firewood pile", "Junk"), ("firewood*", "Firewood", "Junk"), ("coal_bag*", "Sack of coal", "Junk"), ("koryaga*", "Pile of driftwood", "Junk"),
            ("trash_pile*", "Rubbish pile", "Junk"), ("package_garbage*", "Litter", "Junk"), ("goshan_garbage*", "Shopping bag litter", "Junk"),
            ("battary_garbage*", "Old batteries", "Junk"), ("wheel_heap*", "Pile of tyres", "Junk"), ("jcb_tire*", "Tractor tyre", "Junk"),
            ("supermarket_trolley*", "Shopping trolley", "Junk"), ("dumpster_plastic*", "Wheelie bin", "Junk"), ("corpse_scav*", "Dead Scav", "Junk"),
            ("bonfire*", "Campfire", "Junk"), ("barrel_fire*", "Fire barrel", "Junk"), ("garbage_stone*", "Rubble", "Junk"), ("parking_heat*", "Oil stain", "Junk"),
            ("bagdead*", "Body bag", "Junk"),
        };

        private static Regex[] namePatterns;

        private static readonly Dictionary<string, (string name, string kind)> nameCache = new Dictionary<string, (string, string)>();

        // "bed_metal_rezerv (2)" -> "bed_metal_rezerv"
        private static string BaseName(string raw)
        {
            string n = raw ?? "";
            int clone = n.IndexOf(".generated", StringComparison.Ordinal);
            if (clone > 0) n = n.Substring(0, clone);
            n = n.Replace("(Clone)", "");
            n = Regex.Replace(n, @"#\d+$", "");   // a key's path part: "bed (1)#2"
            int bracket = n.IndexOf(" (", StringComparison.Ordinal);
            if (bracket > 0) n = n.Substring(0, bracket);
            return n.Trim();
        }

        private static (string name, string kind) Describe(string raw)
        {
            if (nameCache.TryGetValue(raw ?? "", out var known)) return known;
            if (namePatterns == null)
                namePatterns = NameTable.Select(e => new Regex("^" + Regex.Escape(e.pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray();
            string b = BaseName(raw);
            (string name, string kind) result = (null, null);
            for (int i = 0; i < NameTable.Length; i++)
                if (namePatterns[i].IsMatch(b)) { result = (NameTable[i].name, NameTable[i].kind); break; }
            if (result.name == null) result = (Tidy(b), KindByWords(b));
            return nameCache[raw ?? ""] = result;
        }

        // A name the table doesn't know, by rule: prefixes and version tags off, words split and translated.
        private static readonly string[] DropPrefixes = { "item_barter_", "item_equipment_", "item_container_", "item_food_", "item_meds_", "item_tools_", "item_", "scontainer_", "garb_", "hideout_" };
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["metall"] = "metal", ["matress"] = "mattress", ["battary"] = "battery", ["kanistra"] = "jerrycan", ["provod"] = "cable", ["musor"] = "litter",
            ["stend"] = "stand", ["offis"] = "office", ["plite"] = "tile", ["karpet"] = "rug", ["pallete"] = "pallet", ["helloween"] = "Halloween",
            ["refregirator"] = "fridge", ["koryaga"] = "driftwood", ["vetky"] = "branches", ["conc"] = "concrete", ["frag"] = "fragments", ["tv"] = "TV",
            ["pc"] = "PC", ["cd"] = "CD", ["ak"] = "AK", ["mre"] = "MRE", ["usb"] = "USB", ["hdd"] = "HDD", ["ssd"] = "SSD", ["lfp"] = "LFP",
        };
        private static readonly HashSet<string> DropWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lod", "lod0", "lod1", "lod2", "update", "updated", "rezerv", "rezerve", "reserve", "p", "var", "v", "new", "d", "hipoly", "destroyable", "static", "changed", "physic", "fragmented",
        };

        private static string Tidy(string b)
        {
            string n = b;
            foreach (string p in DropPrefixes)
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { n = n.Substring(p.Length); break; }
            // Split on _ - . and spaces, and between a lower-case letter and an upper-case one (CamelCase).
            n = Regex.Replace(n, "([a-z])([A-Z])", "$1 $2");
            var words = new List<string>();
            foreach (string w in Regex.Split(n, @"[_\-\.\s!]+"))
            {
                if (w.Length == 0) continue;
                string word = Regex.Replace(w, @"\d+$", "");   // bunker_lamp3 -> bunker lamp, carton_10 -> carton
                if (word.Length == 0 || DropWords.Contains(word) || Regex.IsMatch(word, @"^(var|v|lvl|lv)\d*$", RegexOptions.IgnoreCase)) continue;
                words.Add(Words.TryGetValue(word, out string t) ? t : word.ToLowerInvariant());
            }
            if (words.Count == 0) return b;
            string joined = string.Join(" ", words);
            return char.ToUpper(joined[0], CultureInfo.InvariantCulture) + joined.Substring(1);
        }

        // A category for a name the table doesn't know, by the words in it.
        private static readonly (string kind, string[] words)[] KindWords =
        {
            ("Lighting", new[] { "lamp", "light", "candle", "lantern", "bulb", "garland", "torch" }),
            ("Electronics", new[] { "pc_", "monitor", "radio", "computer", "server", "gpu", "phone", "antenna", "battery", "laptop", "keyboard", "printer", "speaker", "router", "electr", "switch", "fuse", "socket" }),
            ("Kitchen", new[] { "food", "bottle", "beer", "juice", "canned", "kettle", "fridge", "stove", "cup", "mug", "dish", "plate" }),
            ("Medical", new[] { "med", "lab_", "vial", "bandage", "pill" }),
            ("Weapons & gear", new[] { "ammo", "weapon", "gun", "mag_", "grenade", "armor", "armour", "helmet", "rig", "backpack", "patron" }),
            ("Gym & range", new[] { "gym", "sport", "target", "dumbbell", "barbell", "training" }),
            ("Storage", new[] { "box", "crate", "case", "container", "safe", "locker", "rack", "shelf", "drawer", "chest", "bag", "barrel", "cabinet", "pallet" }),
            ("Furniture", new[] { "chair", "table", "sofa", "bed", "desk", "bench", "stool", "couch", "carpet", "rug", "curtain", "pillow" }),
            ("Clothing", new[] { "cloth", "jacket", "shirt", "pants", "boot", "shoe", "sock", "glove" }),
            ("Books & paper", new[] { "book", "paper", "magazine", "newspaper", "poster", "folder", "sign" }),
            ("Decoration", new[] { "plant", "flower", "vase", "statue", "trophy", "picture", "frame", "mirror", "clock", "toy" }),
            ("Seasonal", new[] { "halloween", "helloween", "christmas", "pumpkin", "bat_" }),
            ("Bathroom", new[] { "toilet", "sink", "shower", "towel", "soap" }),
            ("Pipes & cables", new[] { "pipe", "tube", "wire", "cable", "vent", "water_" }),
            ("Building", new[] { "door", "wall", "plank", "board", "ladder", "grate", "railing" }),
            ("Tools", new[] { "tool", "wrench", "drill", "tape", "vise", "hammer", "saw", "axe", "screw", "bolt", "nail", "pliers", "bucket", "shovel" }),
            ("Junk", new[] { "heap", "scrap", "garb", "trash", "debris", "rubble", "brick", "tire", "litter" }),
        };

        private static string KindByWords(string b)
        {
            string n = b.ToLowerInvariant();
            foreach (var (kind, words) in KindWords)
                if (words.Any(w => n.Contains(w))) return kind;
            return "Other";
        }

        // A thing's name for the panel and the notes by the mouse.
        private static string Pretty(string raw) => Describe(raw).name;

        private static string KindOf(string raw) => Describe(raw).kind;

        // Not shown in the panel: the scene's colliders, markers, decals and room shells.
        private static bool ForPanel(string raw) => !NotForPanel.IsMatch(BaseName(raw));
    }
}
