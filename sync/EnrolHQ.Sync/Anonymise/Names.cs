namespace EnrolHQ.Sync.Anonymise;

/// <summary>
/// The fake names. The lists and their order must stay the same as Names.First
/// and Names.Last in connector/EnrolHQ.pq, because a fake name is chosen by
/// position.
/// </summary>
internal static class Names
{
    public static readonly string[] First =
    [
        "Adam", "Aiden", "Alex", "Alice", "Amelia", "Andrew", "Anna", "Archie", "Aria", "Ava",
        "Bella", "Benjamin", "Blake", "Caleb", "Callum", "Charlie", "Charlotte", "Chloe", "Claire", "Connor",
        "Daniel", "David", "Dylan", "Edward", "Eleanor", "Eliza", "Ella", "Emily", "Emma", "Ethan",
        "Eva", "Evelyn", "Felix", "Finn", "Freya", "Gabriel", "George", "Georgia", "Grace", "Hannah",
        "Harper", "Harriet", "Harry", "Harvey", "Hazel", "Henry", "Holly", "Hugo", "Hunter", "Imogen",
        "Isaac", "Isabel", "Isla", "Ivy", "Jack", "Jacob", "James", "Jasmine", "Jasper", "Jessica",
        "Joseph", "Joshua", "Julia", "Kate", "Lachlan", "Laura", "Leo", "Levi", "Liam", "Lily",
        "Logan", "Louis", "Lucas", "Lucy", "Luke", "Madeline", "Marcus", "Maria", "Mason", "Matilda",
        "Matthew", "Max", "Maya", "Mia", "Michael", "Mila", "Molly", "Nathan", "Nicholas", "Noah",
        "Oliver", "Olivia", "Oscar", "Patrick", "Penelope", "Phoebe", "Poppy", "Riley", "Robert", "Rose",
        "Ruby", "Ryan", "Samuel", "Sarah", "Scarlett", "Sebastian", "Sienna", "Sophie", "Stella", "Summer",
        "Theodore", "Thomas", "Toby", "Victoria", "Violet", "William", "Willow", "Xavier", "Zachary", "Zoe",
    ];

    public static readonly string[] Last =
    [
        "Adams", "Allen", "Anderson", "Armstrong", "Bailey", "Baker", "Barnes", "Bell", "Bennett", "Black",
        "Bradley", "Brooks", "Brown", "Burns", "Butler", "Cameron", "Campbell", "Carter", "Chapman", "Clark",
        "Cole", "Collins", "Cook", "Cooper", "Cox", "Crawford", "Davies", "Davis", "Dawson", "Dixon",
        "Douglas", "Edwards", "Elliott", "Ellis", "Evans", "Ferguson", "Fisher", "Fletcher", "Ford", "Foster",
        "Fraser", "Gibson", "Gordon", "Graham", "Grant", "Gray", "Green", "Griffiths", "Hall", "Hamilton",
        "Harris", "Harrison", "Hart", "Harvey", "Hayes", "Henderson", "Hill", "Holmes", "Howard", "Hughes",
        "Hunt", "Hunter", "Jackson", "James", "Jenkins", "Johnson", "Johnston", "Jones", "Kelly", "Kennedy",
        "King", "Knight", "Lawrence", "Lee", "Lewis", "Lloyd", "Marshall", "Martin", "Mason", "Matthews",
        "Miller", "Mills", "Mitchell", "Moore", "Morgan", "Morris", "Murphy", "Murray", "Nelson", "Newman",
        "Palmer", "Parker", "Pearce", "Perry", "Phillips", "Powell", "Price", "Reid", "Reynolds", "Richards",
        "Richardson", "Roberts", "Robertson", "Robinson", "Rogers", "Ross", "Russell", "Ryan", "Scott", "Shaw",
        "Simpson", "Smith", "Spencer", "Stevens", "Stewart", "Stone", "Sullivan", "Taylor", "Thomas", "Thompson",
        "Turner", "Walker", "Wallace", "Walsh", "Ward", "Watson", "Webb", "White", "Williams", "Wilson",
    ];
}
